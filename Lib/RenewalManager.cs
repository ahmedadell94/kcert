﻿using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KCert.Lib
{
    public class RenewalManager
    {
        private readonly K8sClient _k8s;
        private readonly KCertClient _kcert;
        private readonly EmailClient _email;
        private readonly IConfiguration _cfg;
        private readonly ILogger<RenewalManager> _log;

        private CancellationTokenSource _cancel;

        public RenewalManager(ILogger<RenewalManager> log, IConfiguration cfg, K8sClient k8s, KCertClient kcert, EmailClient email)
        {
            _cfg = cfg;
            _log = log;
            _k8s = k8s;
            _kcert = kcert;
            _email = email;
        }

        private TimeSpan SleepTime => TimeSpan.FromHours(_cfg.GetValue<int>("Renewals:HoursBetweenChecks"));
        private TimeSpan RenewalThreshold => TimeSpan.FromDays(_cfg.GetValue<int>("Renewals:DaysToRenewal"));

        public async Task StartRenewalServiceAsync()
        {
            while(true)
            {
                _log.LogInformation("Starting up renewal service loop.");
                _cancel = new CancellationTokenSource();
                try
                {
                    await RunLoopAsync(_cancel.Token);
                }
                catch(TaskCanceledException)
                {
                    _log.LogInformation($"Renewal loop cancelled. Restarting.");
                }
            }
        }

        public void RefreshSettings()
        {
            _cancel.Cancel();
        }

        private async Task RunLoopAsync(CancellationToken tok)
        {
            while(true)
            {
                var p = await _kcert.GetConfigAsync();
                if (p?.EnableAutoRenew ?? false)
                {
                    tok.ThrowIfCancellationRequested();
                    await StartRenewalJobAsync(tok);
                }

                await Task.Delay(SleepTime, tok);
            }
        }

        private async Task StartRenewalJobAsync(CancellationToken tok)
        {
            try
            {
                _log.LogInformation("Checking for ingresses that need renewals...");
                foreach (var ingress in await _k8s.GetAllIngressesAsync())
                {
                    tok.ThrowIfCancellationRequested();
                    await TryRenewAsync(ingress, tok);
                }
                _log.LogInformation("Renewal check completed.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, $"Renewal manager job failed: {ex.Message}");
            }
        }

        private async Task TryRenewAsync(Networkingv1beta1Ingress ingress, CancellationToken tok)
        {
            var ns = ingress.Namespace();
            var secretName = ingress.SecretName();
            if (secretName == null)
            {
                return;
            }

            var secret = await _k8s.GetSecretAsync(ns, secretName);
            if (secret == null)
            {
                return;
            }

            var cert = secret.Cert();
            if (DateTime.UtcNow < cert.NotAfter - RenewalThreshold)
            {
                _log.LogInformation($"{ingress.Namespace()} / {ingress.Name()} / {string.Join(',', ingress.Hosts())} doesn't need renewal");
                return;
            }

            tok.ThrowIfCancellationRequested();
            _log.LogInformation($"Renewing: {ingress.Namespace()} / {ingress.Name()} / {string.Join(',', ingress.Hosts())}");
            var result = await _kcert.GetCertAsync(ingress.Namespace(), ingress.Name());
            await _email.NotifyRenewalResultAsync(result);
        }
    }
}