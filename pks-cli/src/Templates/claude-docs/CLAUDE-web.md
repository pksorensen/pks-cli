# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

{{ProjectName}} is a {{TechStack}} web application built with ASP.NET Core and modern web development practices. {{Description}}

## Development Commands

### Building and Running
```bash
# Build the project
cd {{ProjectName}}
dotnet build

# Run locally during development
dotnet run
# or
dotnet watch run

# Build in release mode
dotnet build --configuration Release

# Publish for deployment
dotnet publish --configuration Release --output ./publish
```

### Testing
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run integration tests
dotnet test --filter Category=Integration

# Watch mode for continuous testing
dotnet watch test
```

### Database Commands (if using Entity Framework)
```bash
# Add migration
dotnet ef migrations add [MigrationName]

# Update database
dotnet ef database update

# Drop database
dotnet ef database drop
```

## Architecture

### Core Structure
- **{{ProjectName}}/** - Main source code
  - **Controllers/** - MVC controllers handling HTTP requests
  - **Views/** - Razor views for UI rendering
  - **Models/** - View models and data transfer objects
  - **Services/** - Business logic and application services
  - **Infrastructure/** - Data access and external integrations
  - **wwwroot/** - Static files (CSS, JS, images)

### Key Components

#### ASP.NET Core Web Application
- **Startup.cs** - Application configuration and service registration
- **Controllers/** - MVC controllers with action methods
- **Views/** - Razor views with strongly-typed models
- **Services/** - Business logic and data access services
- **Middleware/** - Custom middleware for cross-cutting concerns

### Available Routes
- `GET /` - Home page
- `GET /Health` - Health check endpoint
- `GET /About` - Application information

### Key Dependencies
- **Microsoft.AspNetCore.App** - ASP.NET Core framework
- **Microsoft.EntityFrameworkCore** - Entity Framework Core (if using)
- **Serilog.AspNetCore** - Structured logging
- **Microsoft.AspNetCore.Authentication** - Authentication middleware

## Development Patterns

### MVC Pattern
- **Models**: Represent data and business logic
- **Views**: Handle the presentation layer with Razor syntax
- **Controllers**: Process requests and coordinate between models and views

### Dependency Injection
- Register services in Startup.cs or Program.cs
- Use constructor injection in controllers and services
- Configure service lifetimes appropriately (Singleton, Scoped, Transient)

### Middleware Pipeline
Configure middleware in the correct order:
1. Exception handling
2. HTTPS redirection
3. Static files
4. Authentication/Authorization
5. Routing
6. Custom middleware
7. Endpoints

### View Organization
- Use layout files for consistent page structure
- Implement partial views for reusable components
- Use strongly-typed views with models
- Organize views by controller in separate folders

## File Organization

```
{{ProjectName}}/
├── Controllers/         # MVC controllers
│   ├── HomeController.cs
│   └── AccountController.cs
├── Views/              # Razor views
│   ├── Home/
│   ├── Account/
│   ├── Shared/
│   └── _ViewStart.cshtml
├── Models/             # View models and DTOs
├── Services/           # Business logic services
├── Infrastructure/     # Data access and configuration
├── wwwroot/           # Static files
│   ├── css/
│   ├── js/
│   └── lib/
├── Areas/             # Feature areas (if using)
├── Data/              # Entity Framework contexts
├── Migrations/        # Database migrations
├── appsettings.json   # Configuration
├── Program.cs         # Application entry point
└── Startup.cs         # Service configuration
```

## Configuration

The application uses ASP.NET Core configuration system:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database={{ProjectName}};Trusted_Connection=true;"
  },
  "ApplicationSettings": {
    "SiteName": "{{ProjectName}}",
    "Version": "1.0.0"
  },
  "Authentication": {
    "Google": {
      "ClientId": "",
      "ClientSecret": ""
    }
  }
}
```

Environment-specific configuration:
- `appsettings.Development.json` - Development settings
- `appsettings.Production.json` - Production settings

## Security Considerations

### Authentication & Authorization
- Implement proper authentication mechanisms
- Use ASP.NET Core Identity for user management
- Configure authorization policies for different user roles
- Protect sensitive endpoints with [Authorize] attributes

### Data Protection
- Use HTTPS for all communications
- Implement CSRF protection with anti-forgery tokens
- Validate all user inputs
- Use parameterized queries to prevent SQL injection

### Session Management
- Configure secure session settings
- Implement proper session timeout
- Use secure cookies with HttpOnly and Secure flags

## Performance Considerations

### Caching
- Implement response caching for static content
- Use in-memory caching for frequently accessed data
- Consider distributed caching for scaled environments

### Optimization
- Minimize HTTP requests with bundling and minification
- Optimize images and static assets
- Use async/await for I/O operations
- Implement proper database query optimization

## Important Instructions

### Development Guidelines
- NEVER create files unless absolutely necessary for achieving the goal
- ALWAYS prefer editing existing files to creating new ones
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested
- Follow MVC patterns and conventions
- Use proper model binding and validation
- Implement proper error handling and logging

### UI/UX Guidelines
- Follow responsive design principles
- Ensure accessibility compliance (WCAG guidelines)
- Use consistent styling and branding
- Implement proper form validation with client and server-side checks
- Provide clear user feedback for actions

### API Design (if applicable)
- Follow RESTful conventions
- Use proper HTTP status codes
- Implement consistent error response format
- Version APIs appropriately
- Document endpoints with XML comments

---

Generated on {{Date}} using PKS CLI Template System