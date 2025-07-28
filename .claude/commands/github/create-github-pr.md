Here are your instructions for the provided query ($ARGUMENTS):

Use subagents to solve the task provided. Make a plan that involves using the

1. github-pr-agent
2. dotnet-build-agent
3. A coding agent

Keep instructing the agents until they are in consensus that the task is solved.

A typical flow can be

1. Asking the github pr agent if everyhing is in order to work on the pr
2. Ask the build agent to build the soltion and if there are errors ask the coding agent to fix them
   -- do so until build agent is happy
3. Ask the github agent to validate that pr checks will pass before commiting things, as these checks should be able to run locally.
4. When all green, ask the github agent to commit our changes if any and start the PR
5. Wait for the github checks to pass and if so, you may conclude that the original query is completed and return the control to the user.
