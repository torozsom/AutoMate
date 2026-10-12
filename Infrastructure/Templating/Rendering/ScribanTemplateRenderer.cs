using Domain.DTO;
using Microsoft.Extensions.Logging;
using Scriban;

namespace Infrastructure.Templating;

/// <summary>
///     Parses and renders individual Scriban templates.
/// </summary>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class ScribanTemplateRenderer(ILogger logger)
{
    /// <summary>
    ///     Renders a manifest rule's template with the supplied model.
    /// </summary>
    /// <param name="rule">Provider detection or template selection rule.</param>
    /// <param name="model">The provider-returned model identity, if known.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the render async derived from the validated input, or null when no
    ///     value is available.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     The selected template cannot be parsed or rendered under its supported contract.
    /// </exception>
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