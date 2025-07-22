# Your First Project Tutorial

This tutorial will walk you through creating your first agentic project with PKS CLI, from installation to deployment. By the end, you'll have a working intelligent API with AI agents helping your development workflow.

## What You'll Build

We'll create a **Smart Todo API** that features:
- ✨ Modern .NET 8 REST API
- 🤖 AI agents for development assistance
- 🔌 MCP integration for AI tool connectivity
- 📚 Comprehensive documentation
- 🚀 Deployment-ready configuration

**Time Required:** 15-20 minutes

## Prerequisites

Before starting, ensure you have:
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) installed
- [PKS CLI](../installation.md) installed (`dotnet tool install -g pks-cli`)
- A code editor (VS Code recommended)
- Basic familiarity with .NET and APIs

## Step 1: Create Your Project

Let's start by creating a new intelligent API project:

```bash
# Create the project with agentic capabilities
pks init smart-todo-api --template api --agentic --mcp --description "An intelligent todo API with AI assistance"
```

**What happens here:**
- PKS CLI creates a new directory `smart-todo-api`
- Sets up a .NET 8 API project structure
- Configures agentic capabilities
- Adds MCP (Model Context Protocol) for AI integration
- Creates documentation and configuration files

You should see output like:
```
🤖 PKS CLI - Professional Agentic Simplifier

✨ Initializing project 'smart-todo-api'...

🔧 Running DotNetProjectInitializer...
   ✓ Created .csproj file
   ✓ Generated Program.cs
   ✓ Added .gitignore

🤖 Running AgenticFeaturesInitializer...
   ✓ Configured agentic capabilities
   ✓ Created agent configuration

🔌 Running McpConfigurationInitializer...
   ✓ Created .mcp.json
   ✓ Generated MCP configuration

📖 Running ClaudeDocumentationInitializer...
   ✓ Created CLAUDE.md
   ✓ Added development guidance

📝 Running ReadmeInitializer...
   ✓ Generated comprehensive README.md

🎉 Project initialized successfully!
```

## Step 2: Explore the Project Structure

Navigate to your new project and explore what was generated:

```bash
cd smart-todo-api
ls -la
```

You'll see a structure like:
```
smart-todo-api/
├── Controllers/           # API controllers
├── Models/               # Data models
├── Services/             # Business logic
├── Program.cs            # Application startup
├── smart-todo-api.csproj # Project configuration
├── README.md             # Project documentation
├── CLAUDE.md             # AI development guidance
├── .mcp.json             # MCP server configuration
├── .gitignore            # Git ignore rules
├── appsettings.json      # App configuration
└── pks.config.json       # PKS CLI configuration
```

**Key files to understand:**
- **Program.cs** - Modern .NET startup with dependency injection
- **CLAUDE.md** - Guidance for AI-assisted development
- **.mcp.json** - Configuration for AI tool integration
- **pks.config.json** - PKS CLI settings

## Step 3: Build and Run

Let's make sure everything works:

```bash
# Restore dependencies and build
dotnet build

# Run the application
dotnet run
```

You should see:
```
Building...
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

Open your browser to `http://localhost:5000/swagger` to see the Swagger UI with your API endpoints.

**Press Ctrl+C to stop the application.**

## Step 4: Create Your First AI Agent

Now let's add an AI agent to help with development:

```bash
# Create a development agent
pks agent create --name DevBot --type developer --description "Helps with API development and testing"
```

Output:
```
🤖 Creating AI Agent...

Agent Configuration:
  • Name: DevBot
  • Type: Developer
  • Specialization: API development and testing
  • Status: Ready

✅ Agent 'DevBot' created successfully!

💡 Next steps:
  - Start the agent: pks agent start DevBot
  - View agent status: pks agent status DevBot
  - List all agents: pks agent list
```

Let's check our agent:
```bash
# List all agents
pks agent list

# Check agent details
pks agent status DevBot
```

## Step 5: Start the MCP Server

The MCP (Model Context Protocol) server enables AI tools to interact with your project:

```bash
# Start the MCP server (in a new terminal)
pks mcp start --port 3000
```

This starts a server that exposes your project's tools and data to AI clients like Claude or GitHub Copilot.

**Keep this terminal open** - the MCP server needs to run continuously for AI integration.

## Step 6: Implement Todo Functionality

Let's add some real functionality to our API. Create a Todo model and controller:

### Create the Todo Model

```bash
# Create Models/Todo.cs
cat > Models/Todo.cs << 'EOF'
namespace SmartTodoApi.Models
{
    public class Todo
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public Priority Priority { get; set; } = Priority.Medium;
    }

    public enum Priority
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }
}
EOF
```

### Create the Todo Controller

```bash
# Create Controllers/TodosController.cs
cat > Controllers/TodosController.cs << 'EOF'
using Microsoft.AspNetCore.Mvc;
using SmartTodoApi.Models;

namespace SmartTodoApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TodosController : ControllerBase
    {
        private static List<Todo> _todos = new List<Todo>
        {
            new Todo { Id = 1, Title = "Learn PKS CLI", Description = "Master the agentic development workflow", Priority = Priority.High },
            new Todo { Id = 2, Title = "Build Smart API", Description = "Create an intelligent todo API", Priority = Priority.Medium }
        };
        private static int _nextId = 3;

        [HttpGet]
        public ActionResult<IEnumerable<Todo>> GetTodos()
        {
            return Ok(_todos);
        }

        [HttpGet("{id}")]
        public ActionResult<Todo> GetTodo(int id)
        {
            var todo = _todos.FirstOrDefault(t => t.Id == id);
            if (todo == null)
                return NotFound();
            
            return Ok(todo);
        }

        [HttpPost]
        public ActionResult<Todo> CreateTodo(Todo todo)
        {
            todo.Id = _nextId++;
            todo.CreatedAt = DateTime.UtcNow;
            _todos.Add(todo);
            
            return CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, todo);
        }

        [HttpPut("{id}")]
        public IActionResult UpdateTodo(int id, Todo todo)
        {
            var existingTodo = _todos.FirstOrDefault(t => t.Id == id);
            if (existingTodo == null)
                return NotFound();

            existingTodo.Title = todo.Title;
            existingTodo.Description = todo.Description;
            existingTodo.Priority = todo.Priority;

            return NoContent();
        }

        [HttpPost("{id}/complete")]
        public IActionResult CompleteTodo(int id)
        {
            var todo = _todos.FirstOrDefault(t => t.Id == id);
            if (todo == null)
                return NotFound();

            todo.IsCompleted = true;
            todo.CompletedAt = DateTime.UtcNow;

            return NoContent();
        }

        [HttpDelete("{id}")]
        public IActionResult DeleteTodo(int id)
        {
            var todo = _todos.FirstOrDefault(t => t.Id == id);
            if (todo == null)
                return NotFound();

            _todos.Remove(todo);
            return NoContent();
        }
    }
}
EOF
```

## Step 7: Test Your API

Let's build and test the updated API:

```bash
# Build the project
dotnet build

# Run the application
dotnet run
```

Now test your API endpoints:

### Using Swagger UI
1. Open `http://localhost:5000/swagger`
2. Try the different endpoints:
   - `GET /api/todos` - List all todos
   - `POST /api/todos` - Create a new todo
   - `GET /api/todos/{id}` - Get specific todo
   - `PUT /api/todos/{id}` - Update a todo
   - `POST /api/todos/{id}/complete` - Mark as complete
   - `DELETE /api/todos/{id}` - Delete a todo

### Using curl
```bash
# Get all todos
curl http://localhost:5000/api/todos

# Create a new todo
curl -X POST http://localhost:5000/api/todos \
  -H "Content-Type: application/json" \
  -d '{"title":"Test PKS CLI","description":"Try out the new features","priority":3}'

# Mark todo as complete
curl -X POST http://localhost:5000/api/todos/1/complete
```

## Step 8: Deploy Your Application

PKS CLI makes deployment intelligent and straightforward:

```bash
# Stop the running application (Ctrl+C)

# Deploy to development environment
pks deploy --environment dev --watch
```

This will:
- Build your application in Release mode
- Create deployment configurations
- Monitor the deployment process
- Provide real-time status updates

## Step 9: Monitor with Agents

Let's use our AI agents to help monitor and improve the application:

```bash
# Start our development agent
pks agent start DevBot

# Check system status with AI insights
pks status --ai-insights --watch

# Get deployment status
pks deploy status --environment dev
```

## What You've Accomplished

Congratulations! You've successfully:

✅ **Created an agentic API project** with PKS CLI  
✅ **Implemented todo functionality** with a RESTful API  
✅ **Set up AI agents** for development assistance  
✅ **Configured MCP integration** for AI tool connectivity  
✅ **Deployed your application** with intelligent monitoring  
✅ **Tested all endpoints** with real data  

## Next Steps

Now that you have a working agentic project:

### 1. Enhance with More Agents
```bash
# Create specialized agents
pks agent create --name TestBot --type testing --description "API testing specialist"
pks agent create --name DocsBot --type documentation --description "Technical documentation expert"
```

### 2. Add Database Integration
```bash
# Initialize with database support
pks init advanced-todo-api --template api --agentic --database postgres --auth jwt
```

### 3. Explore Advanced Features
- **[Working with Agents](working-with-agents.md)** - Deep dive into agent capabilities
- **[MCP Integration](mcp-setup.md)** - Connect with AI tools
- **[Deployment Workflows](deployment-workflows.md)** - Advanced deployment strategies

### 4. Join the Community
- **GitHub**: [pksorensen/pks-cli](https://github.com/pksorensen/pks-cli)
- **Discussions**: Share your experiences and ask questions
- **Issues**: Report bugs or suggest improvements

## Troubleshooting

### Common Issues

**Build Errors:**
```bash
# Clean and rebuild
dotnet clean
dotnet build
```

**Port Already in Use:**
```bash
# Use different port
dotnet run --urls="http://localhost:5001"
```

**Agent Not Starting:**
```bash
# Check agent status
pks agent status DevBot

# Restart agent
pks agent stop DevBot
pks agent start DevBot
```

**MCP Server Issues:**
```bash
# Check if port is available
netstat -an | grep 3000

# Use different port
pks mcp start --port 4000
```

## Summary

You've now experienced the power of agentic development with PKS CLI! This tutorial covered:

- Creating intelligent projects with AI integration
- Setting up development agents for assistance
- Building and deploying modern .NET APIs
- Using MCP for AI tool connectivity
- Monitoring and managing applications

The combination of beautiful terminal UI, intelligent automation, and AI-powered development assistance makes PKS CLI a powerful tool for modern .NET developers.

**Ready to build something bigger?** Try creating a full-stack application with web frontend, or dive into the advanced tutorials to learn more about agent coordination and complex deployment scenarios.

Happy coding! 🚀