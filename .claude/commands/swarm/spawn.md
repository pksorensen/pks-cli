---
description: Spawn a swarm of parallel agents to execute complex tasks
argument-hint: <task description>
allowed-tools:
  - Task
  - TodoWrite
  - Read
  - Write
  - Edit
  - MultiEdit
  - Bash
  - Glob
  - Grep
  - LS
---

# 🐝 SWARM ORCHESTRATION: $ARGUMENTS

You are the SWARM ORCHESTRATOR for the task: **$ARGUMENTS**

## ⚠️ IMPORTANT: MCP Tools Not Yet Implemented

**Note**: The following MCP tools mentioned in this pattern are NOT YET IMPLEMENTED in PKS CLI:

- `mcp__pks__swarm_init`
- `mcp__pks__agent_spawn`
- `mcp__pks__task_orchestrate`
- `mcp__pks__memory_usage`
- `mcp__pks__swarm_monitor`

**Current Approach**: Use the Task tool to spawn agents and TodoWrite for coordination until MCP integration is complete.

## 🚨 CRITICAL INSTRUCTIONS

**MANDATORY**: When orchestrating swarms, you MUST:

1. **SPAWN ALL AGENTS IN ONE BATCH** - Use multiple Task tool calls in a SINGLE message
2. **EXECUTE TASKS IN PARALLEL** - Never wait for one task before starting another
3. **USE BATCHTOOL FOR EVERYTHING** - Multiple operations = Single message with multiple tools
4. **COORDINATE VIA TODOWRITE** - Use comprehensive todo lists for task tracking

## 🎯 AGENT COUNT CONFIGURATION

Analyze the task complexity for "$ARGUMENTS" and determine optimal agent count:

- Simple tasks (1-3 components): 3-4 agents
- Medium tasks (4-6 components): 5-7 agents
- Complex tasks (7+ components): 8-12 agents

## ⚡ PARALLEL EXECUTION PATTERN

### STEP 1: Initialize Swarm & Create Todo List (Single Message!)

```
[Batch Operations]:
  // Create comprehensive todo list
  - TodoWrite { todos: [
      {id: "analyze", content: "Analyze $ARGUMENTS requirements", status: "in_progress", priority: "high"},
      {id: "design", content: "Design system architecture", status: "pending", priority: "high"},
      {id: "implement", content: "Implement core functionality", status: "pending", priority: "high"},
      {id: "test", content: "Write comprehensive tests", status: "pending", priority: "medium"},
      {id: "docs", content: "Create documentation", status: "pending", priority: "low"},
      {id: "review", content: "Code review and optimization", status: "pending", priority: "medium"}
    ]}

  // Spawn agents based on task complexity
  - Task("Architect Agent", "You are the system architect. Design the architecture for: $ARGUMENTS. Update todos as you progress.")
  - Task("Lead Developer", "You are the lead developer. Implement core functionality for: $ARGUMENTS. Update todos as you progress.")
  - Task("Backend Developer", "You are the backend specialist. Build server-side components for: $ARGUMENTS. Update todos as you progress.")
  - Task("Frontend Developer", "You are the frontend specialist. Create UI components for: $ARGUMENTS. Update todos as you progress.")
  - Task("Test Engineer", "You are the QA engineer. Write comprehensive tests for: $ARGUMENTS. Update todos as you progress.")
  - Task("Coordinator", "You are the project coordinator. Monitor progress and ensure smooth execution for: $ARGUMENTS.")
```

### STEP 2: Parallel Execution (Single Message!)

```
[Batch Operations]:
  // Create project structure
  - Bash("mkdir -p project/{src,tests,docs,config}")
  - Bash("mkdir -p project/src/{models,controllers,services,utils}")

  // Initialize project files
  - Write("project/package.json", {...})
  - Write("project/README.md", "# $ARGUMENTS\n\n...")
  - Write("project/.gitignore", "node_modules/\n.env\n...")

  // Read and analyze existing code if needed
  - Read("CLAUDE.md")
  - Glob("**/*.cs")
```

## 📊 VISUAL TASK TRACKING

Monitor progress with this format:

```
📊 Progress Overview
   ├── Total Tasks: X
   ├── ✅ Completed: X (X%)
   ├── 🔄 In Progress: X (X%)
   └── ⭕ Todo: X (X%)

🔄 Active Agents:
   ├── 🟢 Architect: Designing system...
   ├── 🟢 Lead Dev: Implementing core...
   ├── 🟢 Backend: Building APIs...
   ├── 🟡 Frontend: Awaiting design...
   ├── 🟢 Tester: Preparing test suite...
   └── 🟢 Coordinator: Monitoring progress...
```

## ✅ CORRECT PATTERN Example

```javascript
// ✅ CORRECT: Everything in ONE message
[Single Message with Multiple Tools]:
  TodoWrite { todos: [10+ comprehensive todos] }
  Task("Architect", "Design system for: $ARGUMENTS")
  Task("Developer 1", "Implement feature A for: $ARGUMENTS")
  Task("Developer 2", "Implement feature B for: $ARGUMENTS")
  Task("Tester", "Test implementation for: $ARGUMENTS")
  Write("project/src/main.cs", content)
  Write("project/tests/test.cs", content)
  Bash("cd project && dotnet build")
```

## ❌ WRONG PATTERN Example

```javascript
// ❌ WRONG: Sequential messages
Message 1: Task("Spawn one agent")
Message 2: TodoWrite([one todo])
Message 3: Task("Spawn another agent")
Message 4: Write(single file)
// This is inefficient and breaks parallelism!
```

## 📝 TODOWRITE BATCHING RULES

**MANDATORY**:

1. Include 5-10+ todos in SINGLE TodoWrite call
2. NEVER call TodoWrite multiple times in sequence
3. BATCH all updates together
4. Include all priority levels (high, medium, low)

## 🚀 Integration Tips

1. **Start Simple**: Begin with basic task analysis
2. **Scale Gradually**: Add agents based on complexity
3. **Use Todos**: Track all tasks comprehensively
4. **Monitor Progress**: Regular status updates
5. **Batch Operations**: Everything in parallel

## 📞 AGENT COMMUNICATION & COORDINATION

### Communication Protocol

All agents MUST communicate via a shared `.jsonl` file: `.pks/communication.jsonl`

**CRITICAL**: Every agent must:

1. **Introduce Themselves** when spawned:
```json
{"timestamp": "2025-07-26T21:30:00Z", "from": "architect-agent", "type": "introduction", "message": "Architect Agent online, working on system design for: $ARGUMENTS", "status": "active"}
```

2. **Announce Work Progress**:
```json
{"timestamp": "2025-07-26T21:31:00Z", "from": "backend-dev", "type": "status", "message": "Starting API implementation", "task": "implement-apis", "progress": 10}
```

3. **Request Coordination**:
```json
{"timestamp": "2025-07-26T21:32:00Z", "from": "frontend-dev", "to": "architect-agent", "type": "request", "message": "Need API specification before starting UI", "waiting_for": "api-spec"}
```

4. **Report Completion**:
```json
{"timestamp": "2025-07-26T21:35:00Z", "from": "test-engineer", "type": "completion", "message": "Unit tests completed", "task": "write-tests", "progress": 100}
```

### Coordination Rules

1. **Check communication.jsonl** before starting any major task
2. **Write status updates** every significant step
3. **Use exponential backoff** when waiting: 1s, 2s, 4s, 8s, 16s max
4. **Direct messages** use "to" field for specific agents
5. **Avoid duplicate work** by checking what others are doing

### Mandatory Coordinator Agent

Every swarm MUST spawn a **Coordinator Agent** with these responsibilities:

- Monitor `.pks/communication.jsonl` continuously
- Identify conflicts and dependencies
- Redirect agents when stepping on each other
- Ensure progress toward common objective
- Send coordination messages when needed

**Coordinator Messages**:
```json
{"timestamp": "2025-07-26T21:33:00Z", "from": "coordinator", "to": "all", "type": "directive", "message": "Frontend-dev wait for architect to finish API spec. Backend-dev can proceed with database layer."}
```

### Implementation Pattern

```javascript
// Every agent must start with:
Write(".pks/communication.jsonl", JSON.stringify({
  timestamp: new Date().toISOString(),
  from: "agent-name",
  type: "introduction", 
  message: "Agent description and current task",
  status: "active"
}) + "\n")

// Before major work:
Read(".pks/communication.jsonl") // Check what others are doing

// During work:
// Append progress updates (use bash echo >>)

// When waiting:
// Exponential backoff: sleep 1, 2, 4, 8, 16 seconds max
```

## 🔧 Future MCP Integration

When MCP tools are implemented, this command will be updated to use:

- `mcp__pks__swarm_init` for swarm initialization
- `mcp__pks__agent_spawn` for agent creation
- `mcp__pks__memory_usage` for coordination
- `mcp__pks__task_orchestrate` for orchestration

Until then, use Task, TodoWrite, and `.pks/communication.jsonl` for all coordination.

---

**Now executing swarm pattern for**: $ARGUMENTS
