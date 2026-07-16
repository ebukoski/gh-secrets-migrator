using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace SecretsMigrator
{
    public static class Program
    {
        private static readonly OctoLogger _log = new();

        public static async Task Main(string[] args)
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

            await root.InvokeAsync(args);
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

            var branchName = "migrate-secrets";
            var workflow = GenerateWorkflow(targetOrg, targetRepo, branchName, targetHostname);

            var (publicKey, publicKeyId) = await githubApi.GetRepoPublicKey(sourceOrg, sourceRepo);
            await githubApi.CreateRepoSecret(sourceOrg, sourceRepo, publicKey, publicKeyId, "SECRETS_MIGRATOR_PAT", targetPat);

            var defaultBranch = await githubApi.GetDefaultBranch(sourceOrg, sourceRepo);
            var masterCommitSha = await githubApi.GetCommitSha(sourceOrg, sourceRepo, defaultBranch);
            try
            {
                await githubApi.CreateBranch(sourceOrg, sourceRepo, branchName, masterCommitSha);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                _log.LogInformation($"Branch '{branchName}' already exists, updating it...");
                await githubApi.UpdateBranch(sourceOrg, sourceRepo, branchName, masterCommitSha);
            }

            await githubApi.CreateFile(sourceOrg, sourceRepo, branchName, ".github/workflows/migrate-secrets.yml", workflow);

            _log.LogSuccess($"Secrets migration in progress. Check on status at https://github.com/{sourceOrg}/{sourceRepo}/actions");
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
}
