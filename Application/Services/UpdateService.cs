using Octokit;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using System.Text.RegularExpressions;

namespace PtzJoystickControl.Application.Services
{
    public class UpdateService : IUpdateService
    {
        private readonly IGitHubClient _githubClient;
        private readonly string _owner;
        private readonly string _repository;
        private readonly Version _currentVersion;

        public UpdateService(IGitHubClient githubClient, string owner, string repository, Version currentVersion)
        {
            _githubClient = githubClient;
            _owner = owner;
            _repository = repository;
            this._currentVersion = currentVersion;
        }

        public async Task<Update> CheckForUpdate()
        {
            IReadOnlyList<Release> releases;
            try
            {
                // GetAll (not GetLatest): GetLatest ignores pre-releases, which is what this project publishes.
                releases = await _githubClient.Repository.Release.GetAll(_owner, _repository, new ApiOptions { PageSize = 30, PageCount = 1 });
            }
            catch (NotFoundException)
            {
                // Repository without any releases yet: nothing to update to.
                return new Update();
            }
            catch (Exception e)
            {
                return new Update
                {
                    Error = true,
                    ErrorMessage = e.Message
                };
            }

            return Evaluate(
                releases.Select(r => (r.TagName, r.HtmlUrl, r.Draft)),
                _currentVersion);
        }

        /// <summary>Picks the release with the highest version number and compares it with the running version.</summary>
        public static Update Evaluate(IEnumerable<(string Tag, string Url, bool Draft)> releases, Version currentVersion)
        {
            (string Tag, string Url, Version Version)? best = null;
            foreach (var (tag, url, draft) in releases)
            {
                if (draft)
                    continue;
                var match = Regex.Match(tag ?? "", @"\d+\.\d+\.\d+(\.\d+)?");
                if (!match.Success || !Version.TryParse(match.Value, out var version))
                    continue;
                if (best == null || version > best.Value.Version)
                    best = (tag!, url, version);
            }

            if (best == null)
                return new Update();

            return new Update
            {
                Version = best.Value.Tag,
                Uri = Uri.TryCreate(best.Value.Url, UriKind.Absolute, out var uri) ? uri : null,
                Available = best.Value.Version > currentVersion,
            };
        }
    }
}
