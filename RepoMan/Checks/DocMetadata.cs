﻿using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace RepoMan.Checks;

internal sealed class DocMetadata : ICheck
{
    public string Name { get; }
    public string Value { get; }

    public DocMetadata(YamlMappingNode node, State state)
    {
        state.Logger.LogDebugger($"BUILD: Check-metadata-comment");

        Name = node["name"].ToString();
        Value = node["value"].ToString();

        state.Logger.LogTrace($"BUILD: Name: {Name} Value: {Value}");
    }

    public async Task<bool> Run(State state)
    {
        bool result = false;

        state.Logger.LogInformation($"Evaluating comment metadata: {Name} for {Value}");

        if (state.DocIssueMetadata.ContainsKey(Name))
            result = Utilities.MatchRegex(Value, state.DocIssueMetadata[Name], state);

        if (result)
            state.Logger.LogInformation($"PASS");
        else
            state.Logger.LogInformation($"FAIL");

        return await Task.FromResult<bool>(result);
    }
}
