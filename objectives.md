# Objective Queue

In this file i will add tasks that we should continue on when ready.

# hooks command

Add a hooks command that can be used to integrate with claude code hooks.

Research these two links for how the hooks stuff work.  
https://docs.anthropic.com/en/docs/claude-code/hooks-guide
https://docs.anthropic.com/en/docs/claude-code/hooks

We should then make an initialzier for the hooks stuff similar to claude.md and .mcp.json for hooks.

There is a good example here of making use of a smart dispathcer pattern to avoid calling expensive stuff on simple tool calls.
https://claudelog.com/mechanics/hooks/

# mcp command

For the mcp command there is a get startet example here: https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/

I also have example of doing http streamable mcp server here:
var builder = WebApplication.CreateBuilder(args);

// Configure to listen on all interfaces for devcontainer access
builder.WebHost.UseUrls("http://0.0.0.0:3000");

// Register MCP server and discover tools from the current assembly
builder.Services.AddMcpServer().WithHttpTransport(options=>
{
options.Stateless = true; // Enable stateless mode  
}
).WithToolsFromAssembly();

also look into this one: https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server for how we can make mcp server with .net

the readme on https://github.com/modelcontextprotocol/csharp-sdk also have exmaple of exposing a prompt.

The idea is that with the MCP server we should expose a few resources (we dont know yet how to do this in .net, so need investigation) - but we can expose resources to the client (claude code) about what agents are avaible and the status.

We can also make a resource with current tasks for the agent.

The prompts are for slash commands in claude code - so it makes sense that we make a example prompt for github issue triage stuff as example to start.

The mcp server should have tools to create/queue tasks up as well as a prompt file to create a new task.

# Agents structure.

We want to support custom agents defined in the project structure so lets do a .pks folder where we keep such structure for agents:
.pks/agents
--/agent-name/knowledge.md
--/agent-name/persona.md

We should make a command pks agents load that loads this structure into our mcp server state so they are avaible when working.

But we want our claude.md to work as a coordinator/swarm lead that dont do work it self but always sub delegate to tasks/agents. When instructing a new task it should indicate which agent it should use and the first tool call for an agent is to load up using the mcp server the knowledge and persona so it knows how to work and then it work on its task.

So our mcp server also need to be tightly coubled with our agent framework. For now we dont need to persiste this state anyware else than in memory.

# Agents communication

Our MCP server should support that agents can communicate to eachother. We are basically makign a message queue and when a agent is done with a task it can check if there are any messages for it that it should handle.

The orchestrator/swarm lead can spin up an agent if its seeing messages for the agent but its not active (any running task on that agent)

The orchestrator should update its todo to keep monitoring the message queue until its done.

Would be good if we can registre things that should be injected into the initializers, f.eks this one might have information that the claude.md initializer should use to inject something into the file to inform about communication.

# PRD Prompt

We should add a PRD Prompt that helps create a good prd file for project also.

Some things are both commands on the cli and prompts on the MCP server.

Usecases pks prd "Make a prd for this idea...." should make the prd.md file

But we will also incorporate a section of tools in our mcp server for working with requirements and we should have a pks prd load command that can load in a prd and parse it into our structured data in the mcp server.

The idea is that we should be able to ask an Product owner agent which uses these tools to figure out what we should work on next as example.

# CLAUDE.md initializer

Over time people might work on their own claude.md
We should properly let us insprire by this also: https://github.com/SuperClaude-Org/SuperClaude_Framework/blob/master/SuperClaude/Core/CLAUDE.md

its basically just including other files:
@COMMANDS.md @FLAGS.md @PRINCIPLES.md @RULES.md @MCP.md @PERSONAS.md @ORCHESTRATOR.md @MODES.md

That way its easier for project to include their own stuff.

# github initializer / command / prompt

Its possible to create a project scoped pat like this exampel and add this to the mcp servers for the project.

We should investigate if we can use api to make pats for a repo. Also would be good with a resource file on the MCP server about what repo this belong to.

Since we are going to host our MCP server online we actually need to have a initialzied file with some kind of project id so when we setup the mcp server in the project our server knows which project we are working on.

means that our mcp command should take a project id, dnx pks mcp --project-id xxx - and if not provided a new project will be created. not sure yet how the auth flow goes into this, if we can trigger a auth flow to select a project. This is properly related to the pks init command actually. Make a plan for it.

Example of adding github:
claude mcp add-json -s project github '{"type":"http","url":"https://api.githubcopilot.com/mcp/","headers":{"Authorization":"Bearer github_pat_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"}}'

we need to provdie mcp prompts files for devlopment flow with github issues and PRs.

Need to investigate a good bridge between local command files in .claude (ultimately we want to support other tools than claude but github copilot also have local commands) - sounds like a initializer for the init command if it should generate local command files or one just uses the mcp provided promps.

For initializing prompt/command files we need to know the agent tool (github copilot or claude code, or gemini-cli also).
