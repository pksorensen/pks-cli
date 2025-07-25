using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ManualPrdTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var prdService = new PrdService();
            
            Console.WriteLine("Testing PRD Service fixes...\n");
            
            // Test 1: Validation with incomplete document
            Console.WriteLine("1. Testing validation with incomplete document:");
            var tempPath = Path.GetTempFileName() + ".md";
            
            try
            {
                var emptyPrdContent = @"# Empty Project - Product Requirements Document

**Version:** 1.0.0
**Author:** 
**Created:** 2024-01-01
**Updated:** 2024-01-01

## Overview


## Requirements


## User Stories

";
                await File.WriteAllTextAsync(tempPath, emptyPrdContent);
                
                var validationOptions = new PrdValidationOptions
                {
                    FilePath = tempPath,
                    Strictness = "strict",
                    IncludeSuggestions = true
                };
                
                var validation = await prdService.ValidatePrdAsync(validationOptions);
                
                Console.WriteLine($"  IsValid: {validation.IsValid}");
                Console.WriteLine($"  Error count: {validation.Errors?.Count() ?? 0}");
                Console.WriteLine($"  Warning count: {validation.Warnings?.Count() ?? 0}");
                Console.WriteLine($"  Completeness score: {validation.CompletenessScore}");
                
                if (validation.Errors != null)
                {
                    foreach (var error in validation.Errors)
                    {
                        Console.WriteLine($"  Error: {error}");
                    }
                }
                
                Console.WriteLine($"  ✓ Validation test: {(!validation.IsValid && validation.Errors?.Any() == true ? "PASSED" : "FAILED")}\n");
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            
            // Test 2: Status parsing with document
            Console.WriteLine("2. Testing status parsing:");
            var testDoc = CreateTestPrdDocument();
            var statusTempPath = Path.GetTempFileName() + ".md";
            
            try
            {
                await prdService.SavePrdAsync(testDoc, statusTempPath);
                var status = await prdService.GetPrdStatusAsync(statusTempPath);
                
                Console.WriteLine($"  File exists: {status.Exists}");
                Console.WriteLine($"  Total requirements: {status.TotalRequirements}");
                Console.WriteLine($"  Completed requirements: {status.CompletedRequirements}");
                Console.WriteLine($"  Total user stories: {status.TotalUserStories}");
                Console.WriteLine($"  Completion percentage: {status.CompletionPercentage:F1}%");
                
                Console.WriteLine($"  ✓ Status test: {(status.TotalRequirements >= 2 ? "PASSED" : "FAILED")}\n");
            }
            finally
            {
                if (File.Exists(statusTempPath))
                    File.Delete(statusTempPath);
            }
            
            Console.WriteLine("All manual tests completed.");
        }
        
        private static PrdDocument CreateTestPrdDocument()
        {
            return new PrdDocument
            {
                Configuration = new PrdConfiguration
                {
                    ProjectName = "Test Project",
                    Description = "A test project for unit testing",
                    Author = "Test Author",
                    Version = "1.0.0"
                },
                Requirements = new List<PrdRequirement>
                {
                    new()
                    {
                        Id = "REQ-001",
                        Title = "User Authentication",
                        Description = "Users must be able to authenticate",
                        Type = RequirementType.Functional,
                        Priority = RequirementPriority.High,
                        Status = RequirementStatus.Completed
                    },
                    new()
                    {
                        Id = "REQ-002",
                        Title = "Data Storage",
                        Description = "System must store user data",
                        Type = RequirementType.Technical,
                        Priority = RequirementPriority.Critical,
                        Status = RequirementStatus.Draft
                    }
                },
                UserStories = new List<UserStory>
                {
                    new()
                    {
                        Id = "US-001",
                        Title = "User Login",
                        AsA = "user",
                        IWant = "to log into the system",
                        SoThat = "I can access my data",
                        Priority = UserStoryPriority.MustHave,
                        EstimatedPoints = 3
                    }
                },
                Sections = new List<PrdSection>
                {
                    new()
                    {
                        Id = "overview",
                        Title = "Project Overview",
                        Content = "This is a test project overview",
                        Order = 1,
                        Type = SectionType.Overview
                    }
                }
            };
        }
    }
}