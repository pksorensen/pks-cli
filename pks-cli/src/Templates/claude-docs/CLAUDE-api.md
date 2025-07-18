# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

{{ProjectName}} is a {{TechStack}} RESTful API built with ASP.NET Core Web API and modern API development practices. {{Description}}

## Development Commands

### Building and Running
```bash
# Build the project
cd {{ProjectName}}
dotnet build

# Run locally during development
dotnet run
# or with watch for auto-reload
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

# Run unit tests only
dotnet test --filter Category=Unit

# Run integration tests
dotnet test --filter Category=Integration

# Watch mode for continuous testing
dotnet watch test
```

### API Documentation
```bash
# Run with Swagger UI (Development)
dotnet run
# Navigate to: https://localhost:5001/swagger

# Generate OpenAPI specification
dotnet run -- --generate-openapi-spec
```

## Architecture

### Core Structure
- **{{ProjectName}}/** - Main source code
  - **Controllers/** - API controllers with RESTful endpoints
  - **Models/** - DTOs and request/response models
  - **Services/** - Business logic and application services
  - **Infrastructure/** - Data access and external services
  - **Middleware/** - Custom middleware components
  - **Configuration/** - Startup and service configuration

### Key Components

#### ASP.NET Core Web API
- **Controllers/** - API controllers with HTTP endpoints
- **DTOs/** - Data transfer objects for API contracts
- **Services/** - Business logic and application services
- **Repositories/** - Data access layer implementations
- **Middleware/** - Authentication, validation, and error handling
- **Filters/** - Action and result filters for cross-cutting concerns

### Available Endpoints
- `GET /api/health` - Health check endpoint
- `GET /api/version` - API version information
- `GET /swagger` - API documentation (Development only)
- `GET /api/{{project_name}}` - Main resource endpoints

### Key Dependencies
- **Microsoft.AspNetCore.App** - ASP.NET Core Web API framework
- **Swashbuckle.AspNetCore** - Swagger/OpenAPI documentation
- **FluentValidation.AspNetCore** - Input validation
- **AutoMapper** - Object-to-object mapping
- **Microsoft.EntityFrameworkCore** - Data access (if using)
- **Microsoft.AspNetCore.Authentication.JwtBearer** - JWT authentication

## Development Patterns

### RESTful API Design
- Use HTTP verbs appropriately (GET, POST, PUT, DELETE)
- Follow consistent naming conventions for endpoints
- Use proper HTTP status codes (200, 201, 400, 401, 404, 500)
- Implement proper content negotiation
- Version APIs using URL versioning (/api/v1/) or header versioning

### Controller Organization
```csharp
[ApiController]
[Route("api/[controller]")]
public class ResourceController : ControllerBase
{
    // GET api/resource
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ResourceDto>>> GetAll()
    
    // GET api/resource/5
    [HttpGet("{id}")]
    public async Task<ActionResult<ResourceDto>> Get(int id)
    
    // POST api/resource
    [HttpPost]
    public async Task<ActionResult<ResourceDto>> Create(CreateResourceDto dto)
}
```

### Error Handling
- Implement global exception handling middleware
- Use problem details format (RFC 7807) for error responses
- Provide meaningful error messages and codes
- Log errors with correlation IDs for tracing

### Validation
- Use data annotations on DTOs
- Implement FluentValidation for complex validation rules
- Validate at the controller level before processing
- Return validation errors in consistent format

## File Organization

```
{{ProjectName}}/
├── Controllers/         # API controllers
│   ├── ResourceController.cs
│   └── AuthController.cs
├── Models/             # DTOs and request/response models
│   ├── Dtos/
│   ├── Requests/
│   └── Responses/
├── Services/           # Business logic services
│   ├── Interfaces/
│   └── Implementations/
├── Infrastructure/     # Data access and external services
│   ├── Data/
│   ├── Repositories/
│   └── Configurations/
├── Middleware/         # Custom middleware
├── Filters/           # Action and result filters
├── Configuration/     # Startup and service configuration
├── Tests/            # Unit and integration tests
│   ├── Unit/
│   ├── Integration/
│   └── Common/
├── appsettings.json  # Application configuration
├── Program.cs        # Application entry point
└── Startup.cs        # Service configuration
```

## Configuration

The API uses ASP.NET Core configuration system:

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
  "JwtSettings": {
    "SecretKey": "your-secret-key-here",
    "Issuer": "{{ProjectName}}",
    "Audience": "{{ProjectName}}-api",
    "ExpiryMinutes": 60
  },
  "ApiSettings": {
    "Version": "v1",
    "Title": "{{ProjectName}} API",
    "EnableSwagger": true,
    "EnableCors": true
  },
  "ExternalServices": {
    "ServiceA": {
      "BaseUrl": "https://api.servicea.com",
      "ApiKey": "your-api-key"
    }
  }
}
```

## API Documentation

### Swagger/OpenAPI
- Automatic documentation generation with Swashbuckle
- XML comments for detailed endpoint descriptions
- Example requests and responses
- Authentication requirements documentation

### XML Documentation
```csharp
/// <summary>
/// Creates a new resource
/// </summary>
/// <param name="request">The resource creation request</param>
/// <returns>The created resource</returns>
/// <response code="201">Resource created successfully</response>
/// <response code="400">Invalid request data</response>
[HttpPost]
[ProducesResponseType(typeof(ResourceDto), 201)]
[ProducesResponseType(typeof(ProblemDetails), 400)]
public async Task<ActionResult<ResourceDto>> Create(CreateResourceRequest request)
```

## Security Implementation

### Authentication
- JWT Bearer token authentication
- API key authentication for service-to-service calls
- OAuth 2.0 integration for third-party authentication

### Authorization
- Role-based authorization with [Authorize] attributes
- Policy-based authorization for complex scenarios
- Resource-based authorization for fine-grained access control

### Security Headers
- CORS configuration for cross-origin requests
- Security headers middleware (HSTS, X-Frame-Options, etc.)
- Rate limiting to prevent abuse

## Testing Strategy

### Unit Tests
- Test controllers in isolation using mocks
- Test business logic in services independently
- Use xUnit, NUnit, or MSTest frameworks

### Integration Tests
- Test complete API endpoints with real dependencies
- Use TestServer for in-memory testing
- Test authentication and authorization flows

### API Testing
- Use tools like Postman or REST Client for manual testing
- Automated API tests with collection runners
- Load testing for performance validation

## Performance Considerations

### Caching
- Response caching for GET endpoints
- In-memory caching for frequently accessed data
- Distributed caching for scalable deployments

### Database Optimization
- Use async/await for database operations
- Implement proper indexing strategies
- Use pagination for large result sets
- Consider read replicas for query-heavy workloads

## Important Instructions

### Development Guidelines
- NEVER create files unless absolutely necessary for achieving the goal
- ALWAYS prefer editing existing files to creating new ones
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested
- Follow RESTful API conventions
- Use proper HTTP status codes and response formats
- Implement comprehensive error handling and logging

### API Design Principles
- Design APIs to be intuitive and self-documenting
- Use consistent naming conventions across endpoints
- Implement proper versioning strategy
- Provide comprehensive documentation and examples
- Follow the principle of least privilege for security

### Code Quality Standards
- Write comprehensive unit and integration tests
- Use dependency injection for loose coupling
- Implement proper validation for all inputs
- Follow SOLID principles in service design
- Use async/await patterns for I/O operations

---

Generated on {{Date}} using PKS CLI Template System