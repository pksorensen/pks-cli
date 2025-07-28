# Phase 4: Expert Requirements Answers

## Q6: Should we extend the existing ConfigurationService to use persistent storage instead of in-memory dictionary?
**Answer:** Yes - Make a settings file under the user home directory that gets loaded up for this stuff

## Q7: Should we create a custom attribute [SkipFirstTimeWarning] to mark commands that shouldn't show the warning?
**Answer:** Yes

## Q8: Should the warning be displayed before or after the existing DisplayWelcomeBanner() function call?
**Answer:** After

## Q9: Should the warning text include the specific GitHub repository URL for issue reporting?
**Answer:** Yes

## Q10: Should the configuration key for storing acknowledgment be "cli.first-time-warning-acknowledged" or use a different naming pattern?
**Answer:** Yes