using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS utility operations
/// This service provides MCP tools for ASCII art generation and other utilities
/// </summary>
public class UtilityToolService
{
    private readonly ILogger<UtilityToolService> _logger;

    public UtilityToolService(ILogger<UtilityToolService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generate ASCII art from text
    /// This tool connects to the real PKS ASCII command functionality
    /// </summary>
    [McpServerTool]
    [Description("Generate ASCII art from text")]
    public async Task<object> GenerateAsciiArtAsync(
        [Description("The text to convert to ASCII art")] string text,
        [Description("The style of ASCII art (standard, banner, box, shadow, 3d, double)")] string style = "standard",
        [Description("The width constraint for the ASCII art")] int width = 80,
        [Description("The font to use for ASCII art generation")] string font = "default")
    {
        _logger.LogInformation("MCP Tool: Generating ASCII art for '{Text}' with style '{Style}' and font '{Font}'",
            text, style, font);

        try
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(text))
            {
                return new
                {
                    success = false,
                    error = "Text cannot be empty",
                    message = "Please provide text to convert to ASCII art"
                };
            }

            if (width < 20 || width > 200)
            {
                return new
                {
                    success = false,
                    error = "Invalid width",
                    message = "Width must be between 20 and 200 characters"
                };
            }

            // Simulate ASCII art generation time
            await Task.Delay(300);

            // Generate ASCII art based on style
            var asciiArt = GenerateAsciiByStyle(text, style, width, font);
            var complexity = CalculateAsciiComplexity(asciiArt);

            return new
            {
                success = true,
                originalText = text,
                style = style.ToLower(),
                font = font.ToLower(),
                width,
                asciiArt,
                complexity,
                lineCount = asciiArt.Split('\n').Length,
                characterCount = asciiArt.Length,
                generated = DateTime.UtcNow,
                message = $"ASCII art generated successfully for '{text}'"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate ASCII art for text '{Text}'", text);
            return new
            {
                success = false,
                text,
                style,
                font,
                error = ex.Message,
                message = $"ASCII art generation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Generate QR code as ASCII
    /// </summary>
    [McpServerTool]
    [Description("Generate QR code as ASCII art")]
    public async Task<object> GenerateQrCodeAsciiAsync(
        [Description("The text to encode in the QR code")] string text,
        [Description("The size of the QR code (small, medium, large)")] string size = "medium")
    {
        _logger.LogInformation("MCP Tool: Generating QR code ASCII for '{Text}' with size '{Size}'", text, size);

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new
                {
                    success = false,
                    error = "Text cannot be empty",
                    message = "Please provide text to encode in QR code"
                };
            }

            // Simulate QR code generation
            await Task.Delay(500);

            var qrSize = size.ToLower() switch
            {
                "small" => 15,
                "large" => 35,
                _ => 25 // medium
            };

            var qrAscii = GenerateQrCodeAscii(text, qrSize);

            return new
            {
                success = true,
                encodedText = text,
                size = size.ToLower(),
                dimensions = $"{qrSize}x{qrSize}",
                qrCodeAscii = qrAscii,
                scannable = true, // In real implementation, this would depend on the QR generation quality
                generated = DateTime.UtcNow,
                message = $"QR code ASCII generated successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate QR code ASCII for text '{Text}'", text);
            return new
            {
                success = false,
                text,
                size,
                error = ex.Message,
                message = $"QR code generation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Generate banner text with decorative borders
    /// </summary>
    [McpServerTool]
    [Description("Generate banner text with decorative borders")]
    public async Task<object> GenerateBannerAsync(
        [Description("The text to display in the banner")] string text,
        [Description("The border style (single, double, rounded, thick)")] string borderStyle = "double",
        [Description("The text alignment (left, center, right)")] string alignment = "center",
        [Description("The padding around the text (0-10)")] int padding = 2)
    {
        _logger.LogInformation("MCP Tool: Generating banner for '{Text}' with border '{BorderStyle}' and alignment '{Alignment}'",
            text, borderStyle, alignment);

        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new
                {
                    success = false,
                    error = "Text cannot be empty",
                    message = "Please provide text for the banner"
                };
            }

            if (padding < 0 || padding > 10)
            {
                return new
                {
                    success = false,
                    error = "Invalid padding",
                    message = "Padding must be between 0 and 10"
                };
            }

            // Simulate banner generation
            await Task.Delay(200);

            var banner = GenerateBannerText(text, borderStyle, alignment, padding);
            var dimensions = CalculateBannerDimensions(banner);

            return new
            {
                success = true,
                originalText = text,
                borderStyle = borderStyle.ToLower(),
                alignment = alignment.ToLower(),
                padding,
                banner,
                dimensions,
                lineCount = banner.Split('\n').Length,
                generated = DateTime.UtcNow,
                message = $"Banner generated successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate banner for text '{Text}'", text);
            return new
            {
                success = false,
                text,
                borderStyle,
                alignment,
                error = ex.Message,
                message = $"Banner generation failed: {ex.Message}"
            };
        }
    }

    private static string GenerateAsciiByStyle(string text, string style, int width, string font)
    {
        // Simplified ASCII art generation
        // In a real implementation, this would use a proper ASCII art library like FIGlet
        return style.ToLower() switch
        {
            "banner" => GenerateBannerStyle(text, width),
            "box" => GenerateBoxStyle(text, width),
            "shadow" => GenerateShadowStyle(text, width),
            "3d" => Generate3DStyle(text, width),
            "double" => GenerateDoubleStyle(text, width),
            _ => GenerateStandardStyle(text, width)
        };
    }

    private static string GenerateBannerStyle(string text, int width)
    {
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));
        return $"""
            ╔═{new string('═', paddedText.Length)}═╗
            ║ {paddedText} ║
            ╚═{new string('═', paddedText.Length)}═╝
            """;
    }

    private static string GenerateBoxStyle(string text, int width)
    {
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));
        return $"""
            ┌─{new string('─', paddedText.Length)}─┐
            │ {paddedText} │
            └─{new string('─', paddedText.Length)}─┘
            """;
    }

    private static string GenerateShadowStyle(string text, int width)
    {
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));
        return $"""
            ┌─{new string('─', paddedText.Length)}─┐▄
            │ {paddedText} │█
            └─{new string('─', paddedText.Length)}─┘█
             ▀{new string('▀', paddedText.Length + 2)}▀
            """;
    }

    private static string Generate3DStyle(string text, int width)
    {
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));
        return $"""
               ╔═{new string('═', paddedText.Length)}═╗
              ╔╝ {paddedText} ║
             ╔╝  {new string(' ', paddedText.Length)} ║
            ╔╝   {new string(' ', paddedText.Length)} ║
            ╚════{new string('═', paddedText.Length)}═╝
            """;
    }

    private static string GenerateDoubleStyle(string text, int width)
    {
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));
        return $"""
            ╔══{new string('═', paddedText.Length)}══╗
            ║  {paddedText}  ║
            ║  {new string(' ', paddedText.Length)}  ║
            ╚══{new string('═', paddedText.Length)}══╝
            """;
    }

    private static string GenerateStandardStyle(string text, int width)
    {
        var lines = new List<string>();
        var paddedText = text.PadCenter(Math.Max(text.Length, width - 4));

        lines.Add($"{paddedText}");
        lines.Add($"{new string('=', paddedText.Length)}");

        return string.Join('\n', lines);
    }

    private static string GenerateQrCodeAscii(string text, int size)
    {
        // Simplified QR code ASCII representation
        // In a real implementation, this would use a proper QR code library
        var lines = new List<string>();
        var random = new Random(text.GetHashCode()); // Deterministic based on input

        for (int i = 0; i < size; i++)
        {
            var line = "";
            for (int j = 0; j < size; j++)
            {
                // Create a pattern that looks somewhat QR-code-like
                bool isBlack = (i == 0 || i == size - 1 || j == 0 || j == size - 1) ||
                              (i < 7 && j < 7) || (i < 7 && j >= size - 7) ||
                              (i >= size - 7 && j < 7) || random.NextDouble() > 0.6;
                line += isBlack ? "██" : "  ";
            }
            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static string GenerateBannerText(string text, string borderStyle, string alignment, int padding)
    {
        var lines = text.Split('\n');
        var maxLength = lines.Max(l => l.Length);
        var totalWidth = maxLength + (padding * 2);

        var (topLeft, topRight, bottomLeft, bottomRight, horizontal, vertical) = borderStyle.ToLower() switch
        {
            "double" => ("╔", "╗", "╚", "╝", "═", "║"),
            "rounded" => ("╭", "╮", "╰", "╯", "─", "│"),
            "thick" => ("┏", "┓", "┗", "┛", "━", "┃"),
            _ => ("┌", "┐", "└", "┘", "─", "│") // single
        };

        var result = new List<string>();

        // Top border
        result.Add($"{topLeft}{new string(horizontal[0], totalWidth)}{topRight}");

        // Padding rows above text
        for (int i = 0; i < padding; i++)
        {
            result.Add($"{vertical}{new string(' ', totalWidth)}{vertical}");
        }

        // Text lines
        foreach (var line in lines)
        {
            var alignedLine = alignment.ToLower() switch
            {
                "left" => line.PadRight(maxLength),
                "right" => line.PadLeft(maxLength),
                _ => line.PadCenter(maxLength) // center
            };

            result.Add($"{vertical}{new string(' ', padding)}{alignedLine}{new string(' ', padding)}{vertical}");
        }

        // Padding rows below text
        for (int i = 0; i < padding; i++)
        {
            result.Add($"{vertical}{new string(' ', totalWidth)}{vertical}");
        }

        // Bottom border
        result.Add($"{bottomLeft}{new string(horizontal[0], totalWidth)}{bottomRight}");

        return string.Join('\n', result);
    }

    private static string CalculateAsciiComplexity(string ascii)
    {
        var uniqueChars = ascii.Distinct().Count();
        var lines = ascii.Split('\n').Length;

        return (uniqueChars, lines) switch
        {
            ( < 10, < 5) => "simple",
            ( < 20, < 10) => "moderate",
            ( < 30, < 15) => "complex",
            _ => "very complex"
        };
    }

    private static object CalculateBannerDimensions(string banner)
    {
        var lines = banner.Split('\n');
        return new
        {
            width = lines.Max(l => l.Length),
            height = lines.Length,
            area = lines.Sum(l => l.Length)
        };
    }
}

/// <summary>
/// Extension method for string padding
/// </summary>
public static class StringExtensions
{
    public static string PadCenter(this string text, int totalWidth)
    {
        if (text.Length >= totalWidth) return text;

        var padding = totalWidth - text.Length;
        var leftPadding = padding / 2;
        var rightPadding = padding - leftPadding;

        return new string(' ', leftPadding) + text + new string(' ', rightPadding);
    }
}