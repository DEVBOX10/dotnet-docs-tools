# Import GitHub issues to Quest

For teams that work (almost) exclusively in public, GitHub is the natural location to do work item planning and tracking. To save significant time tracking work, we'll automatically copy updates to GitHub issues into Quest for consistency. We'll build a GitHub Action to transfer information from GitHub issues to Quest. The action will run in response to trigger events and update the Quest task board.

This first iteration includes the minimum necessary features to transfer data from GitHub to Quest. From this first iteration, we'll learn what additional features would be useful.

## Installation and use

Once installed, this workflow does the following:

- When a configured "trigger" label is added to an issue, the issue is copied to [Quest](https://dev.azure.com/msft-skilling/Content) as a new user story. In addition, the description in the GitHub issue is amended with a link to the work item. Finally, the configured "trigger" label is removed and a new "linked" label is added.
- When a linked work item is updated, by changing the assignee or closing ths issue, the associated work item is updated.

To install the GitHub actions:

1. ***Add the trigger labels***
   - You'll need to add two labels: one that informs the action to import an issue from GitHub to Quest. The second informs you that an issue has been imported.
1. ***Add a `quest-config.json` file***
   - In the root folder of your repo, create a config file that contains the keys shown [later in this document](#configure-consuming-github-action-workflow). In most cases, you'll modify the Azure DevOps area path, and trigger labels.
1. ***Add the `quest.yml` and `quest-bulk.yml` action workflow files***
   - For an example, see the [`dotnet/docs` installation](https://github.com/dotnet/docs/blob/main/.github/workflows/quest.yml). You'll likely need to change the checks on the labels.
1. ***Add secrets for Azure Dev Ops and Microsoft Open Source Programs Office***
   - You'll need to add two secret tokens to access the OSPO REST APIs and Quest Azure DevOps APIs.
   - *OSPO_KEY*: Generate a PAT [here](https://ossmsft.visualstudio.com/_usersSettings/tokens). UserProfile: Read is the only access needed.
   - *QUEST_KEY*: Generate a PAT [here](https://dev.azure.com/msft-skilling/_usersSettings/tokens). WorkItems: Read/Write and Project & Team: Read/Write access are needed.
     Identity: Read is also needed.
1. Start applying labels.
   - Add the trigger label to any issue, and it will be imported into Quest.

> **Note**: You may need to configure GitHub Actions in your repository settings. For more information, see [Managing GitHub Actions settings for a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository).

## Suggestions for future releases

- [ ] Populate the "GitHub Repo" field in Azure DevOps to make reporting by repository easier.
- [ ] Add Epics (configurable) as a parent of user stories on import.
- [X] Update the label block in Quest when an issue is closed. That way, any "OKR" labels get added when the work item is completed. This would be a simplified version of updating all labels when labels are added or removed.
- [ ] Integrate with Repoman. That tool already performs a number of actions on different events in the repo. The code underlying these events could be ported there.
- [ ] Encapsulate services into their own projects/packages, and share them as needed.
- [X] Use DI where applicable, enabling `IHttpClientFactory`, Polly (for transient fault handling), `IOptions<T>` pattern, and easier testing.

## Triggers

The GitHub action runs as a cron job, or from a manual trigger. In batch mode, it processes all issues updated in the last 5 days. You can specify the look-back time to increase or decrease that time. You can also specify a single issue to process.

There are no plans to update the GitHub issue when changes are made in the Quest User Story. Any employee working in Quest has the expectation that their comments or updates are internal and secure. Automatically propagating that text to a public location creates the risk of publicly disclosing internal information.

## Configure consuming GitHub Action workflow

The consuming repository would ideally define the config file. As an example, it would look like this (with the following file name _quest-config.json_):

```json
{
  "AzureDevOps": {
    "Org": "msft-skilling",
    "Project": "Content",
    "AreaPath": "Production\\Digital and App Innovation\\DotNet and more\\dotnet"
  },
  "ImportTriggerLabel": ":world_map: reQUEST",
  "ImportedLabel": ":pushpin: seQUESTered"
}
```

This config file would override the defaults, enabling consuming repositories to do as they please. In addition to this JSON config, there would need to be three environment variables set:

- `ImportOptions__ApiKeys__GitHubToken`
- `ImportOptions__ApiKeys__OSPOKey`
- `ImportOptions__ApiKeys__QuestKey`

These env vars would be assigned to from the consuming repository secrets, as follows (likely in a workflow file named _quest-import.yml_):

```yaml
env:
  ImportOptions__ApiKeys__GitHubToken: ${{ secrets.GITHUB_TOKEN }}
  ImportOptions__ApiKeys__OSPOKey: ${{ secrets.OSPO_API_KEY }}
  ImportOptions__ApiKeys__QuestKey: ${{ secrets.QUEST_API_KEY }}
```

> **Note:** To configure GitHub Action secrets, see [GitHub Docs: Encrypted secrets](https://docs.github.com/actions/security-guides/encrypted-secrets).

## Field mapping

The GitHub issue fields don't match the Quest fields. Here's how we'll map them, or populate the required Quest fields:

| GitHub field  | Quest field                                                                 |
|---------------|-----------------------------------------------------------------------------|
| Title         | Title                                                                       |
| Description   | Description                                                                 |
| Assigned to   | Assigned to *if FTE*                                                        |
| Comments      | Initial comments will be appended to the the user story description.        |
| Labels        | Labels are added to the description when a user story is created or closed. |

Labels are prefixed with `#` and space characters are replaced with `-` to provide easy search for Quest items with a given label.

There are other Quest fields that don't have GitHub equivalents. They are populated as follows:

- State: The state is set to "new" (by default) when a user story is created.
- Area: Area is configured for each repo. The first pass will set all user stories to the same Area. In future updates, we may use the \*/prod and \*/technology labels to refine areas.
- Iteration: Imported issues are assigned to the current iteration unless org-level projects are used. Currently, this feature is only available in the `dotnet` GitHub org.
- When a GitHub issue is imported into a Quest item, the bot will add a comment on both to link to the associated item:
  - On the GitHub issue:  `Associated WorkItem - nnnnnn` where `nnnnnn` is the Quest work item tag.
  - On the Quest item:  `GitHubOrg/repo#nnnnn`, where `GitHubOrg` is the GitHub organization (e.g. `MicrosoftDocs`), `repo` is the public repository, and `nnnnn` is the issue number.

## Actions performed on processing an issue

This section briefly describes what will happen when processing an issue.

If an issue is has the `reQuest` label, AND the GitHub issue isn't linked to a Quest User story, the following occurs:

- The issue is copied to a new Quest Work item.
  - All comments are copied into the description.
  - All labels are added as a bullet list in the description.
  - A comment is added in the Quest User Story to link to the original GitHub issue.
  - If the issue has already been assigned AND the assignee is an FTE, the Quest User Story is assigned to the same person.
- A comment is added to the GitHub issue to link to the Quest Work item.
- The `reQuest` labels is removed, and the `seQuestered` label is added.

If the GitHub issue has the `seQuestered` label, and is linked to a Quest User Story, the following occurs:

- Update the assigned field in the Quest user story, removing any existing assignees.
- Update the state field, if necessary.
- Update the iteration to the current iteration

> **Note**: The differences between GitHub assignees and Azure Dev Ops assignee introduces the potential for tearing:
>
> - GitHub supports up to 10 assignees on an issue. The first assignee is transferred to Quest.
> - Some FTEs are not assigned as users in Quest. If a GH issue is assigned to a Microsoft employee who isn't a Quest user, that issue is transferred as "unassigned".

If the GitHub issue is CLOSED, and is linked to a Quest User story, the following occurs:

- The Quest user story state is changed to complete.
- The description and labels are imported to reflect any discussion.

## Org project integration

GitHub org-level projects include extra project fields. We've used the "Size" and "Priority" tags to map to the Azure devOps story points and priority, respectively. In addition, where org-level sprint projects are used, the Quest importer looks at the projects an issue is a member of. The Azure DevOps iteration is set to match the most recent GitHub sprint project. In addition, the story points and priority fields are set.

The mapping for story points is as follows:

|GitHub issue Size  | AzDo Story points  |
|-------------------|--------------------|
|🦔 Tiny           |  1                 |
|🐇 Small          |  3                 |
|🐂 Medium         |  5                 |
|🦑 Large          |  8                 |
|🐋 X-Large        | 13                 |
