using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// Where the sample may download from and how much: HTTPS only, to hosts on an allowlist,
    /// checked on every request and every redirect hop, under a per-file and a total size cap.
    /// The defaults are the hosts a Hugging Face download was seen to use (huggingface.co serves
    /// small files and redirects large ones to us.aws.cdn.hf.co); the list is the user's to
    /// change, for a region whose CDN host differs. Hosts match exactly: no wildcards, no IP
    /// addresses, no local names.
    /// </summary>
    [Serializable]
    public sealed class SimReadyHostPolicy
    {
        /// <summary>Observed on 2026-11-04 from one location: the Hub itself and its Xet/LFS CDN.</summary>
        public static readonly string[] DefaultHosts = { "huggingface.co", "us.aws.cdn.hf.co" };

        public const long DefaultMaxFileBytes = 256L * 1024 * 1024;
        public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;

        // Hard ceilings the user's settings cannot exceed.
        public const long CeilingFileBytes = 4L * 1024 * 1024 * 1024;
        public const long CeilingTotalBytes = 16L * 1024 * 1024 * 1024;

        private const string HostsKey = "Unity.USDToolkit.SimReadyCentral.AllowedHosts";
        private const string FileCapKey = "Unity.USDToolkit.SimReadyCentral.MaxFileMB";
        private const string TotalCapKey = "Unity.USDToolkit.SimReadyCentral.MaxTotalMB";

        private static readonly Regex HostName = new Regex(
            @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
            RegexOptions.CultureInvariant);

        [SerializeField] private List<string> hosts = new List<string>(DefaultHosts);
        [SerializeField] private long maxFileBytes = DefaultMaxFileBytes;
        [SerializeField] private long maxTotalBytes = DefaultMaxTotalBytes;

        public IReadOnlyList<string> Hosts => hosts;

        public long MaxFileBytes
        {
            get => maxFileBytes;
            set => maxFileBytes = Math.Max(1L, Math.Min(value, CeilingFileBytes));
        }

        public long MaxTotalBytes
        {
            get => maxTotalBytes;
            set => maxTotalBytes = Math.Max(1L, Math.Min(value, CeilingTotalBytes));
        }

        /// <summary>The defaults: the observed hosts and 256 MB per file, 2 GB in all.</summary>
        public static SimReadyHostPolicy CreateDefault() => new SimReadyHostPolicy();

        /// <summary>
        /// Adds a host the user typed. False with the reason when it is not a plain DNS name
        /// (an IP, a wildcard, a port, a scheme, "localhost") or is already listed.
        /// </summary>
        public bool TryAddHost(string text, out string error)
        {
            if (!TryNormalizeHost(text, out string host, out error))
            {
                return false;
            }

            if (hosts.Contains(host))
            {
                error = $"{host} is already allowed.";
                return false;
            }

            hosts.Add(host);
            return true;
        }

        public bool RemoveHost(string host) => hosts.Remove((host ?? string.Empty).Trim().ToLowerInvariant());

        public void ResetHosts()
        {
            hosts.Clear();
            hosts.AddRange(DefaultHosts);
        }

        /// <summary>A host as the allowlist stores it, or false with the reason it is refused.</summary>
        public static bool TryNormalizeHost(string text, out string host, out string error)
        {
            host = (text ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
            error = null;
            if (host.Length == 0)
            {
                error = "Enter a host name such as cdn-lfs.hf.co.";
                return false;
            }

            if (host.Contains("*"))
            {
                error = "Wildcards are not allowed: list each host.";
                return false;
            }

            if (host.Contains("/") || host.Contains(":") || host.Contains("@"))
            {
                error = "Enter the host name only, without scheme, port or path.";
                return false;
            }

            if (IPAddress.TryParse(host, out _))
            {
                error = "IP addresses are not allowed: downloads go to named HTTPS hosts.";
                return false;
            }

            if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal))
            {
                error = "Local host names are not allowed.";
                return false;
            }

            if (!HostName.IsMatch(host))
            {
                error = $"\"{host}\" is not a valid DNS host name.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The URL may be requested: HTTPS on the default port, no user info, host on the list.
        /// Checked for the first request and again for every redirect.
        /// </summary>
        public bool Permits(Uri uri, out string error)
        {
            error = null;
            if (uri == null || !uri.IsAbsoluteUri)
            {
                error = "Not an absolute URL.";
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"Refused {uri.Scheme}:// (HTTPS only).";
                return false;
            }

            if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = $"Refused {uri.Host}: a port or user info in the URL.";
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            if (!hosts.Contains(host))
            {
                error = $"Refused {host}: not on the download allowlist. Add it under Download settings if you trust it.";
                return false;
            }

            return true;
        }

        // ---- Persistence (PlayerPrefs: per project in the Editor, per app in a player) ----

        /// <summary>The settings saved on this machine, or the defaults.</summary>
        public static SimReadyHostPolicy Load()
        {
            var policy = new SimReadyHostPolicy();
            string saved = PlayerPrefs.GetString(HostsKey, string.Empty);
            if (!string.IsNullOrEmpty(saved))
            {
                policy.hosts.Clear();
                foreach (string entry in saved.Split('\n'))
                {
                    // A stored entry that no longer validates is dropped, not trusted.
                    if (TryNormalizeHost(entry, out string host, out _) && !policy.hosts.Contains(host))
                    {
                        policy.hosts.Add(host);
                    }
                }
            }

            policy.MaxFileBytes = ReadMegabytes(FileCapKey, DefaultMaxFileBytes);
            policy.MaxTotalBytes = ReadMegabytes(TotalCapKey, DefaultMaxTotalBytes);
            return policy;
        }

        public void Save()
        {
            PlayerPrefs.SetString(HostsKey, string.Join("\n", hosts));
            PlayerPrefs.SetString(FileCapKey, (maxFileBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.SetString(TotalCapKey, (maxTotalBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.Save();
        }

        /// <summary>Forgets the saved settings (the next <see cref="Load"/> gives the defaults).</summary>
        public static void ClearSaved()
        {
            PlayerPrefs.DeleteKey(HostsKey);
            PlayerPrefs.DeleteKey(FileCapKey);
            PlayerPrefs.DeleteKey(TotalCapKey);
        }

        public SimReadyHostPolicy Clone() => new SimReadyHostPolicy
        {
            hosts = new List<string>(hosts),
            maxFileBytes = maxFileBytes,
            maxTotalBytes = maxTotalBytes,
        };

        private static long ReadMegabytes(string key, long fallback)
        {
            string text = PlayerPrefs.GetString(key, string.Empty);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long megabytes) && megabytes > 0
                ? megabytes * 1024 * 1024
                : fallback;
        }
    }
}
