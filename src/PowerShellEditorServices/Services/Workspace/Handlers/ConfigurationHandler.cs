// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Logging;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Services.Configuration;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Microsoft.PowerShell.EditorServices.Handlers
{
    internal class PsesConfigurationHandler : DidChangeConfigurationHandlerBase
    {
        private readonly ILogger _logger;
        private readonly WorkspaceService _workspaceService;
        private readonly ConfigurationService _configurationService;
        private readonly PowerShellContextService _powerShellContextService;
        private readonly ILanguageServerFacade _languageServer;
        private bool _profilesLoaded;
        private bool _consoleReplStarted;
        private bool _cwdSet;

        public PsesConfigurationHandler(
            ILoggerFactory factory,
            WorkspaceService workspaceService,
            AnalysisService analysisService,
            ConfigurationService configurationService,
            PowerShellContextService powerShellContextService,
            ILanguageServerFacade languageServer)
        {
            _logger = factory.CreateLogger<PsesConfigurationHandler>();
            _workspaceService = workspaceService;
            _configurationService = configurationService;
            _powerShellContextService = powerShellContextService;
            _languageServer = languageServer;
            ConfigurationUpdated += analysisService.OnConfigurationUpdated;
        }

        public override async Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
        {
            LanguageServerSettingsWrapper incomingSettings = request.Settings.ToObject<LanguageServerSettingsWrapper>();
            if(incomingSettings == null)
            {
                return await Unit.Task.ConfigureAwait(false);
            }

            SendFeatureChangesTelemetry(incomingSettings);

            bool profileLoadingPreviouslyEnabled = _configurationService.CurrentSettings.EnableProfileLoading;
            bool oldScriptAnalysisEnabled =
                _configurationService.CurrentSettings.ScriptAnalysis.Enable ?? false;
            string oldScriptAnalysisSettingsPath =
                _configurationService.CurrentSettings.ScriptAnalysis?.SettingsPath;

            _configurationService.CurrentSettings.Update(
                incomingSettings.Powershell,
                _workspaceService.WorkspacePath,
                _logger);

            if (!this._cwdSet)
            {
                if (!string.IsNullOrEmpty(_configurationService.CurrentSettings.Cwd)
                    && Directory.Exists(_configurationService.CurrentSettings.Cwd))
                {
                    await _powerShellContextService.SetWorkingDirectoryAsync(
                        _configurationService.CurrentSettings.Cwd,
                        isPathAlreadyEscaped: false).ConfigureAwait(false);

                } else if (_workspaceService.WorkspacePath != null
                    && Directory.Exists(_workspaceService.WorkspacePath))
                {
                    await _powerShellContextService.SetWorkingDirectoryAsync(
                        _workspaceService.WorkspacePath,
                        isPathAlreadyEscaped: false).ConfigureAwait(false);
                }

                this._cwdSet = true;
            }

            // We need to load the profiles if:
            // - Profile loading is configured, AND
            //   - Profiles haven't been loaded before, OR
            //   - The profile loading configuration just changed
            if (_configurationService.CurrentSettings.EnableProfileLoading
                && (!this._profilesLoaded || !profileLoadingPreviouslyEnabled))
            {
                await _powerShellContextService.LoadHostProfilesAsync().ConfigureAwait(false);
                this._profilesLoaded = true;
            }

            // Wait until after profiles are loaded (or not, if that's the
            // case) before starting the interactive console.
            if (!this._consoleReplStarted)
            {
                // Start the interactive terminal
                _powerShellContextService.ConsoleReader.StartCommandLoop();
                this._consoleReplStarted = true;
            }

            // Run any events subscribed to configuration updates
            ConfigurationUpdated(this, _configurationService.CurrentSettings);

            // Convert the editor file glob patterns into an array for the Workspace
            // Both the files.exclude and search.exclude hash tables look like (glob-text, is-enabled):
            // "files.exclude" : {
            //     "Makefile": true,
            //     "*.html": true,
            //     "build/*": true
            // }
            var excludeFilePatterns = new List<string>();
            if (incomingSettings.Files?.Exclude != null)
            {
                foreach(KeyValuePair<string, bool> patternEntry in incomingSettings.Files.Exclude)
                {
                    if (patternEntry.Value) { excludeFilePatterns.Add(patternEntry.Key); }
                }
            }
            if (incomingSettings.Search?.Exclude != null)
            {
                foreach(KeyValuePair<string, bool> patternEntry in incomingSettings.Search.Exclude)
                {
                    if (patternEntry.Value && !excludeFilePatterns.Contains(patternEntry.Key)) { excludeFilePatterns.Add(patternEntry.Key); }
                }
            }
            _workspaceService.ExcludeFilesGlob = excludeFilePatterns;

            // Convert the editor file search options to Workspace properties
            if (incomingSettings.Search?.FollowSymlinks != null)
            {
                _workspaceService.FollowSymlinks = incomingSettings.Search.FollowSymlinks;
            }

            return await Unit.Task.ConfigureAwait(false);
        }

        private void SendFeatureChangesTelemetry(LanguageServerSettingsWrapper incomingSettings)
        {
            var configChanges = new Dictionary<string, bool>();
            // Send telemetry if the user opted-out of ScriptAnalysis
            if (incomingSettings.Powershell.ScriptAnalysis.Enable == false &&
                _configurationService.CurrentSettings.ScriptAnalysis.Enable != incomingSettings.Powershell.ScriptAnalysis.Enable)
            {
                configChanges["ScriptAnalysis"] = incomingSettings.Powershell.ScriptAnalysis.Enable ?? false;
            }

            // Send telemetry if the user opted-out of CodeFolding
            if (!incomingSettings.Powershell.CodeFolding.Enable &&
                _configurationService.CurrentSettings.CodeFolding.Enable != incomingSettings.Powershell.CodeFolding.Enable)
            {
                configChanges["CodeFolding"] = incomingSettings.Powershell.CodeFolding.Enable;
            }

            // Send telemetry if the user opted-out of the prompt to update PackageManagement
            if (!incomingSettings.Powershell.PromptToUpdatePackageManagement &&
                _configurationService.CurrentSettings.PromptToUpdatePackageManagement != incomingSettings.Powershell.PromptToUpdatePackageManagement)
            {
                configChanges["PromptToUpdatePackageManagement"] = incomingSettings.Powershell.PromptToUpdatePackageManagement;
            }

            // Send telemetry if the user opted-out of Profile loading
            if (!incomingSettings.Powershell.EnableProfileLoading &&
                _configurationService.CurrentSettings.EnableProfileLoading != incomingSettings.Powershell.EnableProfileLoading)
            {
                configChanges["ProfileLoading"] = incomingSettings.Powershell.EnableProfileLoading;
            }

            // Send telemetry if the user opted-in to Pester 5+ CodeLens
            if (!incomingSettings.Powershell.Pester.UseLegacyCodeLens &&
                _configurationService.CurrentSettings.Pester.UseLegacyCodeLens != incomingSettings.Powershell.Pester.UseLegacyCodeLens)
            {
                // From our perspective we want to see how many people are opting in to this so we flip the value
                configChanges["Pester5CodeLens"] = !incomingSettings.Powershell.Pester.UseLegacyCodeLens;
            }

            // No need to send any telemetry since nothing changed
            if (configChanges.Count == 0)
            {
                return;
            }

            _languageServer.Window.SendTelemetryEvent(new TelemetryEventParams
            {
                ExtensionData = new PsesTelemetryEvent
                {
                    EventName = "NonDefaultPsesFeatureConfiguration",
                    Data = JObject.FromObject(configChanges)
                }
            });
        }

        public event EventHandler<LanguageServerSettings> ConfigurationUpdated;
    }
}
