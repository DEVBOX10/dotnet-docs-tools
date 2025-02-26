using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using YamlDotNet.RepresentationModel;
using Octokit;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using Grpc.Core;

[assembly: InternalsVisibleTo("RepoManConfigTest")]

namespace RepoMan;

public class Function1
{
    public const string EventTypeIssue = "issues";
    public const string EventTypePullRequest = "pull_request";
    public const string EventTypeComment = "issue_comment";
    public const int SchemaVersionMinimum = 1;
    public const string RulesFileName = ".repoman.yml";

    private readonly ILogger _logger;

    public Function1(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<Function1>();
    }

    [Function("Function1")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
    {
        try
        {
            State state = new() { Logger = _logger };
            
            _logger.LogInformation($"RepoMan v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

            // Prep for auth
            string? token = Environment.GetEnvironmentVariable("GithubToken", EnvironmentVariableTarget.Process);

            if (string.IsNullOrEmpty(token))
            {
                string error = "GithubToken setting is missing";
                _logger.LogError(error);
                
                return  req.CreateBadRequestResponse(error);
            }

            // Github client
            state.Client = new GitHubClient(new ProductHeaderValue("adegeo-ms-repoman", "1.0"))
            {
                Credentials = new Credentials(token)
            };

            // Validate signature of payload
            string requestBody = new StreamReader(req.Body).ReadToEnd();
            string eventType = req.Headers.GetValues("X-GitHub-Event").First();

            if (!IsSecure(requestBody, req.Headers.GetValues("X-Hub-Signature").First(), _logger))
            {
                string error = "Sig is missing";
                _logger.LogError(error);
                return req.CreateBadRequestResponse(error);
            }

            // =================
            // EVENT: Issue
            // =================
            if (eventType == EventTypeIssue)
            {
                IssueEventPayload issuePayload = new Octokit.Internal.SimpleJsonSerializer().Deserialize<IssueEventPayload>(requestBody);

                _logger.LogInformation($"Type: Issue \nId: {issuePayload.Issue.Number} \nAction: {issuePayload.Action} \nRepo: {issuePayload.Repository.CloneUrl}");

                state.RepositoryId = issuePayload.Repository.Id;
                state.RepositoryName = issuePayload.Repository.Name;
                state.RepositoryOwner = issuePayload.Repository.Owner.Login;
                state.RequestType = RequestType.Issue;

                (bool Success, bool GhalFileExists) rulesResult = await ReadRepoRulesFile(state);

                // Check if rules failed to load
                if (!rulesResult.Success)
                {
                    string error;

                    if (rulesResult.GhalFileExists)
                        error = "Legacy .ghal.rules.json exists in repository. Contact adegeo@ms to upgrade.";
                    else
                        error = $"The rules file ({RulesFileName}) is missing in repository.";

                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                // Make sure rules version is correct
                if (!ValidateVersionInformation(state))
                {
                    string error = "Rules file is out-of-date. Contact adegeo@ms to upgrade.";
                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                state.EventAction = issuePayload.Action;
                state.PullRequest = issuePayload.Issue.PullRequest != null ? await state.Client.PullRequest.Get(issuePayload.Repository.Id, issuePayload.Issue.Number) : null;
                state.Issue = issuePayload.Issue;
                state.Comment = null;
                state.EventPayload = JObject.Parse(requestBody);
                state.IssuePrBody = issuePayload.Issue.Body ?? string.Empty;

                if (state.PullRequest != null)
                {
                    state.IsPullRequest = true;
                    state.PullRequestFiles = (await state.Client.PullRequest.Files(state.RepositoryId, state.PullRequest.Number)).ToArray();
                    state.PullRequestReviews = (await state.Client.PullRequest.Review.GetAll(state.RepositoryId, state.PullRequest.Number)).ToArray();
                }
            }

            // =================
            // EVENT: Pull Request
            // =================
            else if (eventType == EventTypePullRequest)
            {
                PullRequestEventPayload pullPayload = new Octokit.Internal.SimpleJsonSerializer().Deserialize<PullRequestEventPayload>(requestBody);

                _logger.LogInformation($"Type: PullRequest \nId: {pullPayload.PullRequest.Number} \nAction: {pullPayload.Action} \nRepo: {pullPayload.Repository.CloneUrl}");

                state.RepositoryId = pullPayload.Repository.Id;
                state.RepositoryName = pullPayload.Repository.Name;
                state.RepositoryOwner = pullPayload.Repository.Owner.Login;
                state.RequestType = RequestType.PullRequest;

                (bool Success, bool GhalFileExists) rulesResult = await ReadRepoRulesFile(state);

                // Check if rules failed to load
                if (!rulesResult.Success)
                {
                    string error;

                    if (rulesResult.GhalFileExists)
                        error = "Legacy .ghal.rules.json exists in repository. Contact adegeo@ms to upgrade.";
                    else
                        error = $"The rules file ({RulesFileName}) is missing in repository.";

                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                // Make sure rules version is correct
                if (!ValidateVersionInformation(state))
                {
                    string error = "Rules file is out-of-date. Contact adegeo@ms to upgrade.";
                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                state.IsPullRequest = true;
                state.EventAction = pullPayload.Action;
                state.PullRequest = pullPayload.PullRequest;
                state.Issue = await state.Client.Issue.Get(pullPayload.Repository.Id, pullPayload.Number);
                state.Comment = null;
                state.EventPayload = JObject.Parse(requestBody);
                state.IssuePrBody = pullPayload.PullRequest.Body ?? string.Empty;
                state.PullRequestFiles = (await state.Client.PullRequest.Files(state.RepositoryId, state.PullRequest.Number)).ToArray();
                state.PullRequestReviews = (await state.Client.PullRequest.Review.GetAll(state.RepositoryId, state.PullRequest.Number)).ToArray();
            }

            // =================
            // EVENT: Comment
            // =================
            else if (eventType == EventTypeComment)
            {
                IssueCommentPayload commentPayload = new Octokit.Internal.SimpleJsonSerializer().Deserialize<IssueCommentPayload>(requestBody);

                _logger.LogInformation($"Type: PullRequest \nId: {commentPayload.Issue.Number} \nAction: {commentPayload.Action} \nRepo: {commentPayload.Repository.CloneUrl}");

                state.RepositoryId = commentPayload.Repository.Id;
                state.RepositoryName = commentPayload.Repository.Name;
                state.RepositoryOwner = commentPayload.Repository.Owner.Login;
                state.RequestType = RequestType.Comment;

                (bool Success, bool GhalFileExists) rulesResult = await ReadRepoRulesFile(state);

                // Check if rules failed to load
                if (!rulesResult.Success)
                {
                    string error;

                    if (rulesResult.GhalFileExists)
                        error = "Legacy .ghal.rules.json exists in repository. Contact adegeo@ms to upgrade.";
                    else
                        error = $"The rules file ({RulesFileName}) is missing in repository.";

                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                // Make sure rules version is correct
                if (!ValidateVersionInformation(state))
                {
                    string error = "Rules file is out-of-date. Contact adegeo@ms to upgrade.";
                    state.Logger.LogError(error);
                    return req.CreateBadRequestResponse(error);
                }

                state.EventAction = commentPayload.Action;
                state.PullRequest = commentPayload.Issue.PullRequest != null ? await state.Client.PullRequest.Get(commentPayload.Repository.Id, commentPayload.Issue.Number) : null;
                state.Issue = commentPayload.Issue;
                state.Comment = commentPayload.Comment;
                state.EventPayload = JObject.Parse(requestBody);
                state.IssuePrBody = commentPayload.Comment.Body ?? string.Empty;

                if (state.PullRequest != null)
                {
                    state.IsPullRequest = true;
                    state.PullRequestFiles = (await state.Client.PullRequest.Files(state.RepositoryId, state.PullRequest.Number)).ToArray();
                    state.PullRequestReviews = (await state.Client.PullRequest.Review.GetAll(state.RepositoryId, state.PullRequest.Number)).ToArray();
                }
            }

            else
            {
                state.Logger.LogError($"Event isn't supported {eventType}");
                return req.CreateBadRequestResponse();
            }

            // Check if a magic label was sent, modify the state accordingly
            await RerunLabelCheck(state);

            // Based on the first comment of an issue, the body
            state.LoadCommentMetadata(state.IssuePrBody);

            // Check rules for event+action combination
            if (state.RepoRulesYaml.Exists(eventType))
            {
                YamlMappingNode eventNode = state.RepoRulesYaml[eventType].AsMappingNode();

                if (eventNode.Children.ContainsKey(state.EventAction))
                {
                    bool remappedEvent = false;

                    restart_node_check:
                    YamlNode actionNode = eventNode[state.EventAction];

                    // Remapping
                    if (actionNode.NodeType == YamlNodeType.Scalar)
                    {
                        // We've remapped once, don't allow it again.
                        if (remappedEvent)
                        {
                            state.Logger.LogError($"Remapping already happened once. Can't remap an event into another remap.");
                            return req.CreateBadRequestResponse();
                        }

                        state.Logger.LogInformation($"Remap found in rules. From: {state.EventAction} To: {actionNode}");

                        // Prevent circular reference
                        if (state.EventAction == actionNode.ToString())
                        {
                            state.Logger.LogError($"Remapped to self.");
                            return req.CreateBadRequestResponse();
                        }

                        state.EventAction = actionNode.ToString();
                        goto restart_node_check;
                    }
                    else if (actionNode.NodeType != YamlNodeType.Sequence)
                    {
                        state.Logger.LogError($"Event should use a sequence.");
                        return req.CreateBadRequestResponse();
                    }

                    state.Logger.LogInformation($"Processing action: {state.EventAction}");

                    await Runner.Build(actionNode.AsSequenceNode(), state).Run(state);
                    await state.RunPooledActions();
                }
                else
                    state.Logger.LogInformation($"Action {state.EventAction} not defined in rules. Nothing to do.");
            }
            else
                state.Logger.LogInformation($"Event {eventType} not defined in rules. Nothing to do.");

            return req.CreateResponse(HttpStatusCode.OK);
        }
        catch (Exception e)
        {
            _logger.LogError("Problem parsing: " + e);
            return req.CreateBadRequestResponse();
        }
    }

    private static async Task RerunLabelCheck(State state)
    {
        if (state.EventAction == "labeled")
        {
            Label? magicLabel = state.Issue.Labels.FirstOrDefault(l => l.Name.StartsWith("rerun-action-", StringComparison.OrdinalIgnoreCase));

            if (magicLabel != null)
            {
                state.EventAction = magicLabel.Name.ToLower().Substring("rerun-action-".Length);
                state.Logger.LogInformation($"Magic label found: {magicLabel.Name}; Reprocessing issue {state.Issue.Number} as {state.EventAction}");

                // Remove the trigger labels
                await GithubCommand.RemoveLabels(new string[] { magicLabel.Name }, state.Issue.Labels, state);

                // Refresh the issue.
                state.Issue = await state.Client.Issue.Get(state.RepositoryId, state.Issue.Number);

                // Refresh the PR
                if (state.RequestType == RequestType.PullRequest)
                {
                    state.PullRequest = await state.Client.PullRequest.Get(state.RepositoryId, state.Issue.Number);
                    state.IssuePrBody = state.PullRequest.Body ?? string.Empty;
                }
                else if (state.RequestType == RequestType.Issue)
                {
                    state.IssuePrBody = state.Issue?.Body ?? string.Empty;
                }
                else // Comment
                {
                    state.IssuePrBody = state.Comment?.Body ?? string.Empty;
                }
            }
        }
    }

    /// <summary>
    /// Logs the version and schema version of the repo rules file.
    /// </summary>
    /// <param name="state">The function state object.</param>
    /// <returns><see langword="true"/> when the values are found and the schema is up-to-date; otherwise <see langword="false"/>.</returns>
    private static bool ValidateVersionInformation(State state)
    {
        try
        {
            int revision = state.RepoRulesYaml["revision"].ToInt();
            int schemaVersion = state.RepoRulesYaml["schema-version"].ToInt();
            string contact = state.RepoRulesYaml["owner-ms-alias"].ToString();

            state.Logger.LogInformation($"Repo rules file [version]: {revision} [schema-version]: {schemaVersion} [contact]: {contact}");

            if (schemaVersion < SchemaVersionMinimum)
            {
                state.Logger.LogError($"schema-version is out-of-date: {schemaVersion}, must be: {SchemaVersionMinimum}");
                return false;
            }

            return true;
        }
        catch
        {
            state.Logger.LogError("Unable to read version or schema-version from rules file.");
            return false;
        }
    }

    /// <summary>
    /// Reads the rules file from the repo into the <paramref name="state"/> parameter.
    /// </summary>
    /// <param name="state">The state object.</param>
    /// <returns>A bool, bool tuple to indicate the success of finding the rules file and the old rules (ghal) files from the repo.</returns>
    private static async Task<(bool Success, bool GhalFileExists)> ReadRepoRulesFile(State state)
    {
        // Make sure old config file doesn't exist:
        try
        {
            IReadOnlyList<RepositoryContent> oldConfig = await state.Client.Repository.Content.GetAllContents(state.RepositoryId, ".ghal.rules.json");
            return (false, true);
        }
        catch (Octokit.NotFoundException)
        {
            // Do nothing, we want this to happen.
        }
        catch
        {
            state.Logger.LogError("Unknown error testing for .ghal.rules.json");
            return (false, true);
        }

        // Get repo settings
        try
        {
            IReadOnlyList<RepositoryContent> rulesResponse = await state.Client.Repository.Content.GetAllContents(state.RepositoryId, RulesFileName);
            string repoRulesFile = rulesResponse[0].Content;

            ///* HACK This is broken... github is adding byte 63 to the start of the file which breaks the parser
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(repoRulesFile);
            if (bytes[0] == 63)
                repoRulesFile = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(1));

            if (repoRulesFile == null)
                return (false, false);

            // Read config for the repo
            state.Logger.LogInformation($"Reading repo rules file: {RulesFileName}");
            using StringReader reader = new StringReader(repoRulesFile);
            YamlStream parser = new YamlStream();
            parser.Load(reader);

            // Convert string content into YAML object
            state.RepoRulesYaml = (YamlMappingNode)parser.Documents[0].RootNode;

            // Read settings
            state.LoadSettings(state.RepoRulesYaml["config"]);

            return (true, false);
        }
        catch (Octokit.NotFoundException)
        {
            return (false, false);
        }
        catch
        {
            state.Logger.LogError("Unknown error retreiving or loading the yaml file");
            return (false, false);
        }
    }

    /// <summary>
    /// Validates the body matches the hash of the signature.
    /// </summary>
    /// <param name="body">Payload body.</param>
    /// <param name="signature">The signature of the request.</param>
    /// <param name="log">Logging object.</param>
    /// <returns><see langword="true"/> when validated; otherwise <see langword="false"/>.</returns>
    private static bool IsSecure(string body, string signature, ILogger log)
    {
        const string Sha1Prefix = "sha1=";

        if (!signature.StartsWith(Sha1Prefix)) return false;
        signature = signature.Substring(Sha1Prefix.Length);

        string? token = System.Environment.GetEnvironmentVariable("SecretToken", EnvironmentVariableTarget.Process);

        if (token == null)
        {
            log.LogError("SecretToken environment variable is missing.");
            return false;
        }

        byte[] secret = System.Text.Encoding.ASCII.GetBytes(token);
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(body);

        using System.Security.Cryptography.HMACSHA1 sha1 = new(secret);
        string result = ToHexString(sha1.ComputeHash(payloadBytes));

        if (string.Equals(result, signature, StringComparison.OrdinalIgnoreCase))
        {
            log.LogTrace($"signature check OK\n    sent: {signature}\ncomputed: {result}");
            return true;
        }

        log.LogError($"signature check failed\n    sent: {signature}\ncomputed: {result}");

        return false;

        static string ToHexString(byte[] bytes)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(bytes.Length * 2);

            foreach (byte b in bytes)
                builder.AppendFormat("{0:x2}", b);

            return builder.ToString();
        }
    }
}
