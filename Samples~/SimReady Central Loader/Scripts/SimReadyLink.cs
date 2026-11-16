using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Unity.USDToolkit.Samples.SimReadyCentral
{
    /// <summary>
    /// A Hugging Face file link as SimReady Central's "Copy URL" gives it
    /// (<c>https://huggingface.co/datasets/onrobot/simready-grippers/blob/&lt;sha&gt;/onrobot_rg2/simready_usd/onrobot_rg2.usda</c>),
    /// parsed into repo, revision and path. Accepted:
    /// <c>https://huggingface.co/{datasets|models}/{owner}/{repo}/{blob|resolve|raw|tree}/{revision}/{path}</c>.
    /// A <c>tree</c> link names a folder; everything else names one file. Anything else, plain
    /// HTTP or another host included, is rejected with the reason.
    /// </summary>
    public sealed class SimReadyLink
    {
        public const string Host = "huggingface.co";

        private static readonly Regex Name = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant);
        private static readonly Regex RevisionPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha = new Regex(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

        private SimReadyLink()
        {
        }

        /// <summary>"datasets" or "models": the URL segment and the Hub API's repo type.</summary>
        public string RepoType { get; private set; }

        public string Owner { get; private set; }

        public string Repo { get; private set; }

        /// <summary>"blob", "resolve", "raw" or "tree", as pasted.</summary>
        public string Kind { get; private set; }

        /// <summary>The branch, tag or commit the link names.</summary>
        public string Revision { get; private set; }

        /// <summary>Repo-relative path, unescaped, '/' separated, no leading slash.</summary>
        public string Path { get; private set; }

        public string RepoId => Owner + "/" + Repo;

        /// <summary>The revision is a full commit SHA, so it never moves.</summary>
        public bool IsCommit => Sha.IsMatch(Revision);

        /// <summary>The link names a folder (a <c>tree</c> link).</summary>
        public bool IsFolder => Kind == "tree";

        /// <summary>
        /// The asset's folder in the repo: the first path segment (<c>onrobot_rg2</c>), which holds
        /// the asset, its payloads and its metadata. Nothing outside it is fetched.
        /// </summary>
        public string PackageFolder
        {
            get
            {
                int slash = Path.IndexOf('/');
                return slash < 0 ? (IsFolder ? Path : string.Empty) : Path.Substring(0, slash);
            }
        }

        /// <summary>Parses a pasted link. False with the reason when it is not one this sample accepts.</summary>
        public static bool TryParse(string text, out SimReadyLink link, out string error)
        {
            link = null;
            error = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "Paste a Hugging Face file link from SimReady Central.";
                return false;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
            {
                error = "Not a URL.";
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                error = $"Only https:// links are accepted (got {uri.Scheme}://).";
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            if (host != Host && host != "www." + Host)
            {
                error = $"Only {Host} links are accepted (got {uri.Host}).";
                return false;
            }

            if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "The link may not carry a port or user information.";
                return false;
            }

            // The path as pasted: Uri collapses "." and ".." segments, which would hide a traversal.
            // The query (?download=true) and fragment do not change which file it is.
            int authority = text.IndexOf("://", StringComparison.Ordinal) + 3;
            int pathStart = text.IndexOf('/', authority);
            string rawPath = pathStart < 0 ? string.Empty : text.Substring(pathStart);
            int cut = rawPath.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
            {
                rawPath = rawPath.Substring(0, cut);
            }

            string[] raw = rawPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var segments = new List<string>(raw.Length);
            foreach (string part in raw)
            {
                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(part);
                }
                catch (UriFormatException)
                {
                    error = "The link's path is not valid percent-encoding.";
                    return false;
                }

                segments.Add(decoded);
            }

            if (segments.Count < 5 || (segments[0] != "datasets" && segments[0] != "models"))
            {
                error = "Expected https://huggingface.co/{datasets|models}/{owner}/{repo}/{blob|resolve|raw|tree}/{revision}/{path}.";
                return false;
            }

            string kind = segments[3];
            if (kind != "blob" && kind != "resolve" && kind != "raw" && kind != "tree")
            {
                error = $"Expected blob, resolve, raw or tree after the repo name (got \"{kind}\").";
                return false;
            }

            if (!Name.IsMatch(segments[1]) || !Name.IsMatch(segments[2]))
            {
                error = "The owner or repo name has characters Hugging Face does not use.";
                return false;
            }

            if (!RevisionPattern.IsMatch(segments[4]))
            {
                error = "The revision has characters this sample does not accept (a branch, tag or commit SHA is expected).";
                return false;
            }

            var pathParts = segments.GetRange(5, segments.Count - 5);
            if (pathParts.Count == 0 && kind != "tree")
            {
                error = "The link names a revision but no file.";
                return false;
            }

            foreach (string part in pathParts)
            {
                if (!SimReadyAssetPaths.IsSafeSegment(part))
                {
                    error = $"The path segment \"{part}\" is not allowed.";
                    return false;
                }
            }

            string path = string.Join("/", pathParts);
            if (kind != "tree" && !SimReadyAssetPaths.IsUsdLayer(path))
            {
                error = "The link must name a .usd, .usda or .usdc file (or use a /tree/ folder link).";
                return false;
            }

            link = new SimReadyLink
            {
                RepoType = segments[0],
                Owner = segments[1],
                Repo = segments[2],
                Kind = kind,
                Revision = segments[4],
                Path = path,
            };
            return true;
        }

        /// <summary>The same link, pinned to <paramref name="commit"/>.</summary>
        public SimReadyLink AtCommit(string commit)
        {
            if (!Sha.IsMatch(commit ?? string.Empty))
            {
                throw new ArgumentException("A 40-character commit SHA is expected.", nameof(commit));
            }

            return new SimReadyLink
            {
                RepoType = RepoType,
                Owner = Owner,
                Repo = Repo,
                Kind = Kind,
                Revision = commit,
                Path = Path,
            };
        }

        /// <summary>The same link naming another file of the repo (a resolved root layer).</summary>
        public SimReadyLink WithFile(string repoPath)
        {
            return new SimReadyLink
            {
                RepoType = RepoType,
                Owner = Owner,
                Repo = Repo,
                Kind = "resolve",
                Revision = Revision,
                Path = repoPath,
            };
        }

        /// <summary>Where a file of the repo downloads from, at this link's revision (always /resolve/).</summary>
        public string ResolveUrl(string repoPath) =>
            $"https://{Host}/{RepoType}/{Owner}/{Repo}/resolve/{Uri.EscapeDataString(Revision)}/{EscapePath(repoPath)}";

        /// <summary>The link rewritten for download.</summary>
        public string DownloadUrl => ResolveUrl(Path);

        /// <summary>The Hub API call that names the commit a branch or tag is at.</summary>
        public string RevisionApiUrl => $"https://{Host}/api/{RepoType}/{Owner}/{Repo}/revision/{Uri.EscapeDataString(Revision)}";

        /// <summary>The Hub API listing of a folder (recursive), at this link's revision.</summary>
        public string TreeApiUrl(string folder) =>
            $"https://{Host}/api/{RepoType}/{Owner}/{Repo}/tree/{Uri.EscapeDataString(Revision)}/{EscapePath(folder)}?recursive=true";

        /// <summary>The repo's page, for the info panel.</summary>
        public string RepoUrl => $"https://{Host}/{RepoType}/{Owner}/{Repo}";

        public override string ToString() => $"{RepoId}@{Revision}/{Path}";

        private static string EscapePath(string path)
        {
            var builder = new StringBuilder();
            foreach (string part in (path ?? string.Empty).Split('/'))
            {
                if (part.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('/');
                }

                builder.Append(Uri.EscapeDataString(part));
            }

            return builder.ToString();
        }
    }
}
