using Core.DTO;
using Microsoft.Extensions.Logging;
using Scriban;

namespace Services.Templating;

/// <summary>
///     Parses and renders individual Scriban templates.
/// </summary>
internal sealed class ScribanTemplateRenderer(ILogger logger)
{
    /// <summary>
    ///     Renders a manifest rule's template with the supplied model.
    /// </summary>
    public async Task<string?> RenderAsync(TemplateManifestRuleDto rule, object model,
        CancellationToken cancellationToken)
    {
        var templatePath = TemplatePaths.ResolveTemplatePath(rule.TemplateFile);

        if (!File.Exists(templatePath))
        {
            logger.LogWarning("[TemplateService] The template file '{TemplateFile}' does not exist!",
                rule.TemplateFile);
            return null;
        }

        try
        {
            var templateText = await File.ReadAllTextAsync(templatePath, cancellationToken);
            var parsedTemplate = Template.Parse(templateText);

            if (parsedTemplate.HasErrors)
            {
                var errors = string.Join(", ", parsedTemplate.Messages.Select(message => message.Message));
                logger.LogError("[TemplateService] Could not parse Scriban template: {TemplateFile}. Errors: {Errors}",
                    rule.TemplateFile, errors);
                throw new InvalidOperationException($"Could not parse file: {rule.TemplateFile}, errors: {errors}");
            }

            var renderedContent = await parsedTemplate.RenderAsync(model);
            logger.LogInformation("[TemplateService] Successfully rendered: {OutputFile}", rule.OutputFile);
            return renderedContent;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[TemplateService] Template generation cancelled for '{OutputFile}'.", rule.OutputFile);
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[TemplateService] Unexpected error occurred while generating template: {TemplateFile}",
                rule.TemplateFile);
            throw;
        }
    }
}