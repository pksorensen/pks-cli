#r "/workspace/pks-cli/src/bin/Debug/net8.0/pks-cli.dll"
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.IO;
using System.Threading.Tasks;

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

Console.WriteLine("Manual test completed.");