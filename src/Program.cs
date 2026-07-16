using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace SecretsMigrator
{
    public static class Program
    {
        private static readonly OctoLogger _log = new();

        public static async Task<int> Main(string[] args)
        {
            var root = new RootCommand
            {
                Description = "Migrates all secrets from one GitHub repo to another."
            };

            var sourceOrg = new Option<string>("--source-org")
            {
                IsRequired = true
            };
            var sourceRepo = new Option<string>("--source-repo")
            {
                IsRequired = true
            };
            var targetOrg = new Option<string>("--target-org")
            {
                IsRequired = true
            };
            var targetRepo = new Option<string>("--target-repo")
            {
                IsRequired = true
            };
            var sourcePat = new Option<string>("--source-pat")
            {
                IsRequired = true
            };
            var targetPat = new Option<string>("--target-pat")
            {
                IsRequired = true
            };
            var verbose = new Option("--verbose")
            {
                IsRequired = false
            };
            var targetHostname = new Option<string>("--target-hostname", () => "github.com")
            {
                IsRequired = false
            };

            root.AddOption(sourceOrg);
            root.AddOption(sourceRepo);
            root.AddOption(targetOrg);
            root.AddOption(targetRepo);
            root.AddOption(sourcePat);
            root.AddOption(targetPat);
            root.AddOption(verbose);
            root.AddOption(targetHostname);

            root.Handler = CommandHandler.Create<string, string, string, string, string, string, bool, string>(Invoke);

            return await root.InvokeAsync(args);
        }

        public static async Task Invoke(string sourceOrg, string sourceRepo, string targetOrg, string targetRepo, string sourcePat, string targetPat, bool verbose = false, string targetHostname = "github.com")
        {
            _log.Verbose = verbose;

            _log.LogInformation("Migrating Secrets...");
            _log.LogInformation($"SOURCE ORG: {sourceOrg}");
            _log.LogInformation($"SOURCE REPO: {sourceRepo}");
            _log.LogInformation($"TARGET ORG: {targetOrg}");
            _log.LogInformation($"TARGET REPO: {targetRepo}");
            _log.LogInformation($"TARGET HOSTNAME: {targetHostname}");

            var githubClient = new GithubClient(_log, sourcePat);
            var githubApi = new GithubApi(githubClient, "https://api.github.com");

            var secretNames = await githubApi.GetRepoSecretNames(sourceOrg, sourceRepo);
            var migratableSecrets = secretNames
                .Where(name => name != "github_token" && name != "SECRETS_MIGRATOR_PAT")
                .ToList();

            if (migratableSecrets.Count == 0)
            {
                _log.LogInformation($"No secrets to migrate from {sourceOrg}/{sourceRepo}. Skipping workflow creation.");
                return;
            }

            _log.LogInformation($"Found {migratableSecrets.Count} secret(s) to migrate.");

            var defaultBranch = await githubApi.GetDefaultBranch(sourceOrg, sourceRepo);
            var masterCommitSha = await githubApi.GetCommitSha(sourceOrg, sourceRepo, defaultBranch);

            // Unique per run: avoids clobbering a real branch of this name, and disambiguates our run below
            var branchName = $"migrate-secrets-{masterCommitSha[..7]}";
            var workflow = GenerateWorkflow(targetOrg, targetRepo, branchName, targetHostname);

            var (publicKey, publicKeyId) = await githubApi.GetRepoPublicKey(sourceOrg, sourceRepo);
            await githubApi.CreateRepoSecret(sourceOrg, sourceRepo, publicKey, publicKeyId, "SECRETS_MIGRATOR_PAT", targetPat);

            try
            {
                await githubApi.CreateBranch(sourceOrg, sourceRepo, branchName, masterCommitSha);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                _log.LogInformation($"Branch '{branchName}' already exists, updating it...");
                await githubApi.UpdateBranch(sourceOrg, sourceRepo, branchName, masterCommitSha);
            }

            // Pushing the workflow file triggers the run; its commit sha is the run's head_sha, matched below
            var workflowCommitSha = await githubApi.CreateFile(sourceOrg, sourceRepo, branchName, ".github/workflows/migrate-secrets.yml", workflow);

            _log.LogInformation($"Secrets migration workflow triggered. Following progress at https://github.com/{sourceOrg}/{sourceRepo}/actions");

            var conclusion = await WaitForWorkflow(githubApi, sourceOrg, sourceRepo, branchName, workflowCommitSha);

            // Delete it ourselves before returning; the run's own cleanup is best-effort, and a
            // lingering branch trips the downstream branch-count validation
            await DeleteBranchIfExists(githubApi, sourceOrg, sourceRepo, branchName);

            if (conclusion == "success")
            {
                _log.LogSuccess($"Secrets migration completed for {sourceOrg}/{sourceRepo}.");
                return;
            }

            _log.LogError($"Secrets migration did not succeed (conclusion: {conclusion ?? "timed out"}). See https://github.com/{sourceOrg}/{sourceRepo}/actions");
            throw new SecretsMigrationException($"Secrets migration workflow concluded '{conclusion ?? "timeout"}'.");
        }

        private const int PollIntervalSeconds = 10;
        private const int RunAppearTimeoutSeconds = 60;
        private const int RunCompleteTimeoutMinutes = 5;
        private const string WorkflowFileName = "migrate-secrets.yml";

        // Returns the run's conclusion, or null if it never appeared or completed within the timeouts
        private static async Task<string> WaitForWorkflow(GithubApi githubApi, string org, string repo, string branch, string headSha)
        {
            _log.LogInformation("Waiting for the secrets migration workflow to start...");

            long? runId = null;
            var appearDeadline = DateTime.UtcNow.AddSeconds(RunAppearTimeoutSeconds);
            while (DateTime.UtcNow < appearDeadline)
            {
                var runs = await githubApi.GetWorkflowRuns(org, repo, WorkflowFileName, branch);
                var match = runs.FirstOrDefault(r => r.HeadSha == headSha);
                if (match.Id != 0) // default tuple (Id 0) => no match yet
                {
                    runId = match.Id;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds));
            }

            if (runId is null)
            {
                _log.LogWarning("Timed out waiting for the secrets migration workflow run to appear.");
                return null;
            }

            _log.LogInformation($"Workflow run {runId} started; waiting for it to complete...");

            var completeDeadline = DateTime.UtcNow.AddMinutes(RunCompleteTimeoutMinutes);
            while (DateTime.UtcNow < completeDeadline)
            {
                var (status, conclusion) = await githubApi.GetWorkflowRun(org, repo, runId.Value);
                _log.LogVerbose($"Workflow run {runId} status: {status}");
                if (status == "completed")
                {
                    _log.LogInformation($"Workflow run {runId} completed with conclusion: {conclusion}");
                    return conclusion;
                }
                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds));
            }

            _log.LogWarning($"Timed out waiting for workflow run {runId} to complete.");
            return null;
        }

        // Idempotent: 404/422 means the run's own cleanup already deleted the branch
        private static async Task DeleteBranchIfExists(GithubApi githubApi, string org, string repo, string branch)
        {
            _log.LogInformation($"Removing temporary branch '{branch}'...");
            try
            {
                await githubApi.DeleteBranch(org, repo, branch);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity)
            {
                _log.LogVerbose($"Branch '{branch}' was already removed.");
            }
        }

        private static string GenerateWorkflow(string targetOrg, string targetRepo, string branchName, string targetHostname = "github.com")
        {
            var result = $@"
name: move-secrets
on:
  push:
    branches: [ ""{branchName}"" ]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - name: Migrate Secrets
        # Use the pre-installed GitHub CLI (gh) to migrate secrets.
        # This is more reliable than installing custom crypto packages.
        run: |
          $targetRepo = ""${{{{ env.TARGET_ORG }}}}/${{{{ env.TARGET_REPO }}}}""
          
          # Convert the secrets JSON into a PowerShell object
          $secrets = $env:REPO_SECRETS | ConvertFrom-Json
          
          # Loop through each secret property in the object
          $secrets.psobject.properties | ForEach-Object {{
            $secretName = $_.Name
            $secretValue = $_.Value
            
            # Skip the special tokens used by the workflow itself
            if ($secretName -ne ""github_token"" -and $secretName -ne ""SECRETS_MIGRATOR_PAT"") {{
              Write-Output ""Migrating Secret: $secretName to $targetRepo""
              
              # Use gh secret set. It handles fetching the public key and encryption automatically.
              # We pipe the secret value to the command to avoid it appearing in logs.
              $secretValue | gh secret set $secretName --repo $targetRepo
            }}
          }}
        env:
          GH_HOST: '{targetHostname}'
          GH_TOKEN: ${{{{ secrets.SECRETS_MIGRATOR_PAT }}}}
          GH_ENTERPRISE_TOKEN: ${{{{ secrets.SECRETS_MIGRATOR_PAT }}}}
          REPO_SECRETS: ${{{{ toJSON(secrets) }}}}
          TARGET_ORG: '{targetOrg}'
          TARGET_REPO: '{targetRepo}'
        shell: pwsh

      - name: Clean up temporary branch and secret
        # Use 'continue-on-error' in case the branch is already protected or deleted.
        continue-on-error: true
        run: |
          Write-Output ""Deleting migration branch...""
          gh api ""repos/${{{{ github.repository }}}}/git/refs/heads/{branchName}"" -X DELETE

          Write-Output ""Deleting secrets migrator PAT from source repository...""
          gh secret delete SECRETS_MIGRATOR_PAT --repo ${{{{ github.repository }}}}
        env:
          GH_TOKEN: ${{{{ secrets.SECRETS_MIGRATOR_PAT }}}}
        shell: pwsh
";

            return result;
        }
    }

    public class SecretsMigrationException : Exception
    {
        public SecretsMigrationException(string message) : base(message) { }
    }
}
