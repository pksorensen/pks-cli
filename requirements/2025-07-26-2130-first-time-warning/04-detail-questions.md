# Phase 4: Expert Requirements Questions

Based on deep analysis of the PKS CLI codebase, here are the most pressing technical questions:

## Q6: Should we extend the existing ConfigurationService to use persistent storage instead of in-memory dictionary?
**Default if unknown:** Yes (user acknowledgment must persist across CLI sessions)

The current ConfigurationService uses an in-memory dictionary which won't persist the user's acknowledgment between CLI invocations.

## Q7: Should we create a custom attribute [SkipFirstTimeWarning] to mark commands that shouldn't show the warning?
**Default if unknown:** Yes (following the user's suggestion for attribute-based command marking)

This would provide a clean way to mark specific commands (like MCP, hooks) that should skip the warning, similar to existing skip logic.

## Q8: Should the warning be displayed before or after the existing DisplayWelcomeBanner() function call?
**Default if unknown:** After (user specified "as the last thing" with the banner)

Integration point in Program.cs needs to determine the exact placement within the banner display flow.

## Q9: Should the warning text include the specific GitHub repository URL for issue reporting?
**Default if unknown:** Yes (issue mentions users are "welcome to look at the code and report issues on github")

This would provide users with the direct link to pksorensen/pks-cli repository for issue reporting.

## Q10: Should the configuration key for storing acknowledgment be "cli.first-time-warning-acknowledged" or use a different naming pattern?
**Default if unknown:** Yes, use "cli.first-time-warning-acknowledged" (follows existing configuration key patterns like "cluster.endpoint")

This follows the existing dot-notation pattern used in the ConfigurationService.