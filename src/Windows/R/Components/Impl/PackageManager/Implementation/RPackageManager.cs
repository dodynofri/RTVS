﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Common.Core.Disposables;
using Microsoft.Common.Core.Events;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.PackageManager.Model;
using Microsoft.R.Components.Settings;
using Microsoft.R.Components.Settings.Mirrors;
using Microsoft.R.Host.Client;
using Microsoft.R.Host.Client.Host;
using Microsoft.R.Host.Client.Session;
using Newtonsoft.Json.Linq;
using static System.FormattableString;

namespace Microsoft.R.Components.PackageManager.Implementation {
    internal class RPackageManager : IRPackageManager {
        private readonly IRSessionProvider _sessionProvider;
        private readonly IRSession _pmSession;
        private readonly IRSession _replSession;
        private readonly IRSettings _settings;
        private readonly DisposableBag _disposableBag;
        private readonly DirtyEventSource _loadedPackagesEvent;
        private readonly DirtyEventSource _installedPackagesEvent;
        private readonly DirtyEventSource _availablePackagesEvent;

        public event EventHandler LoadedPackagesInvalidated {
            add => _loadedPackagesEvent.Event += value;
            remove => _loadedPackagesEvent.Event -= value;
        }

        public event EventHandler InstalledPackagesInvalidated {
            add => _installedPackagesEvent.Event += value;
            remove => _installedPackagesEvent.Event -= value;
        }

        public event EventHandler AvailablePackagesInvalidated {
            add => _availablePackagesEvent.Event += value;
            remove => _availablePackagesEvent.Event -= value;
        }

        public bool IsRemoteSession => _pmSession.IsRemote;

        public RPackageManager(IRInteractiveWorkflow interactiveWorkflow, Action dispose) {
            _sessionProvider = interactiveWorkflow.RSessions;
            _pmSession = _sessionProvider.GetOrCreate(SessionNames.PackageManager);
            _replSession = interactiveWorkflow.RSession;
            _settings = interactiveWorkflow.Services.GetService<IRSettings>();
            _loadedPackagesEvent = new DirtyEventSource(this);
            _installedPackagesEvent = new DirtyEventSource(this);
            _availablePackagesEvent = new DirtyEventSource(this);

            _disposableBag = DisposableBag.Create<RPackageManager>()
                .Add(dispose)
                .Add(() => _sessionProvider.BrokerChanged -= BrokerChanged)
                .Add(() => _replSession.Mutated -= RSessionMutated)
                .Add(() => _replSession.PackagesInstalled -= PackagesInstalled)
                .Add(() => _replSession.PackagesRemoved -= PackagesRemoved);

            _sessionProvider.BrokerChanged += BrokerChanged;
            _replSession.Mutated += RSessionMutated;
            _replSession.PackagesInstalled += PackagesInstalled;
            _replSession.PackagesRemoved += PackagesRemoved;
        }

        public async Task<IReadOnlyList<RPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default (CancellationToken)) {
            _installedPackagesEvent.Reset();
            return await GetPackagesAsync(_pmSession.InstalledPackagesAsync, cancellationToken);
        }

        public async Task<IReadOnlyList<RPackage>> GetAvailablePackagesAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _availablePackagesEvent.Reset();
            return await GetPackagesAsync(_pmSession.AvailablePackagesAsync, cancellationToken);
        }

        public async Task InstallPackageAsync(string name, string libraryPath, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var request = await _replSession.BeginInteractionAsync(cancellationToken:cancellationToken)) {
                if (string.IsNullOrEmpty(libraryPath)) {
                    await request.InstallPackageAsync(name);
                } else {
                    await request.InstallPackageAsync(name, libraryPath);
                }
            }
        }

        public Task<PackageLockState> UninstallPackageAsync(string name, string libraryPath, CancellationToken cancellationToken = default(CancellationToken)) =>
            _replSession.EvaluateAsync<PackageLockState>(
                Invariant($"rtvs:::package_uninstall({name.ToRStringLiteral()}, {libraryPath.ToRStringLiteral()})"), REvaluationKind.Normal, cancellationToken);

        public Task<PackageLockState> UpdatePackageAsync(string name, string libraryPath, CancellationToken cancellationToken = default(CancellationToken)) =>
            _replSession.EvaluateAsync<PackageLockState>(
                Invariant($"rtvs:::package_update({name.ToRStringLiteral()}, {libraryPath.ToRStringLiteral()})"), REvaluationKind.Normal, cancellationToken);

        public async Task LoadPackageAsync(string name, string libraryPath, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var request = await _replSession.BeginInteractionAsync(cancellationToken: cancellationToken)) {
                if (string.IsNullOrEmpty(libraryPath)) {
                    await request.LoadPackageAsync(name);
                } else {
                    await request.LoadPackageAsync(name, libraryPath);
                }
            }
        }

        public async Task UnloadPackageAsync(string name, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var request = await _replSession.BeginInteractionAsync(cancellationToken: cancellationToken)) {
                await request.UnloadPackageAsync(name);
            }
        }

        public async Task<string[]> GetLoadedPackagesAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            _loadedPackagesEvent.Reset();
            var result = await WrapRException(_replSession.LoadedPackagesAsync(cancellationToken));
            return result.Select(p => (string)((JValue)p).Value).ToArray();
        }

        public async Task<string> GetLibraryPathAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            var result = await WrapRException(_replSession.LibraryPathsAsync(cancellationToken));
            return result.Select(p => p.ToRPath()).FirstOrDefault();
        }

        public Task<PackageLockState> GetPackageLockStateAsync(string name, string libraryPath, CancellationToken cancellationToken = default(CancellationToken)) 
            => _replSession.EvaluateAsync<PackageLockState>(
                 Invariant($"rtvs:::package_lock_state({name.ToRStringLiteral()}, {libraryPath.ToRStringLiteral()})"), REvaluationKind.Normal, cancellationToken);

        private async Task<IReadOnlyList<RPackage>> GetPackagesAsync(Func<Task<JArray>> queryFunc, CancellationToken cancellationToken) {
            // Fetching of installed and available packages is done in a
            // separate package query session to avoid freezing the REPL.
            try {
                await _pmSession.EnsureHostStartedAsync(new RHostStartupInfo(), null, cancellationToken: cancellationToken);
                await _pmSession.SetVsCranSelectionAsync(CranMirrorList.UrlFromName(_settings.CranMirror), cancellationToken);
                await _pmSession.SetCodePageAsync(_settings.RCodePage, cancellationToken);

                // Get the repos and libpaths from the REPL session and set them
                // in the package query session
                var repositories = (await DeparseRepositoriesAsync());
                if (repositories != null) {
                    await WrapRException(_pmSession.ExecuteAsync($"options(repos=eval(parse(text={repositories.ToRStringLiteral()})))", cancellationToken));
                }

                var libraries = (await DeparseLibrariesAsync());
                if (libraries != null) { 
                    await WrapRException(_pmSession.ExecuteAsync($".libPaths(eval(parse(text={libraries.ToRStringLiteral()})))", cancellationToken));
                }

                var result = await WrapRException(queryFunc());
                return result.Select(p => p.ToObject<RPackage>()).ToList().AsReadOnly();

            } catch (RHostDisconnectedException ex) {
                throw new RPackageManagerException(Resources.PackageManager_TransportError, ex);
            }
        }

        private async Task<string> DeparseRepositoriesAsync() {
            try {
                return await WrapRException(_replSession.EvaluateAsync<string>("rtvs:::deparse_str(getOption('repos'))", REvaluationKind.Normal));
            } catch(RHostDisconnectedException) {
                return null;
            }
        }

        private async Task<string> DeparseLibrariesAsync() {
            try {
                return await WrapRException(_replSession.EvaluateAsync<string>("rtvs:::deparse_str(.libPaths())", REvaluationKind.Normal));
            } catch (RHostDisconnectedException) {
                return null;
            }
        }

        public void Dispose() => _disposableBag.TryDispose();

        private async Task WrapRException(Task task) {
            try {
                await task;
            } catch (RException ex) {
                throw new RPackageManagerException(string.Format(CultureInfo.InvariantCulture, Resources.PackageManager_EvalError, ex.Message), ex);
            }
        }

        private async Task<T> WrapRException<T>(Task<T> task) {
            try {
                return await task;
            } catch (RException ex) {
                throw new RPackageManagerException(string.Format(CultureInfo.InvariantCulture, Resources.PackageManager_EvalError, ex.Message), ex);
            }
        }

        private void BrokerChanged(object sender, EventArgs e) {
            _availablePackagesEvent.FireOnce();
            _installedPackagesEvent.FireOnce();
            _loadedPackagesEvent.FireOnce();
        }

        private void PackagesInstalled(object sender, EventArgs e) {
            _installedPackagesEvent.FireOnce();
            _loadedPackagesEvent.FireOnce();
        }
        
        private void PackagesRemoved(object sender, EventArgs e) {
            _installedPackagesEvent.FireOnce();
            _loadedPackagesEvent.FireOnce();
        }
        
        private void RSessionMutated(object sender, EventArgs e) {
            if (_sessionProvider.IsConnected) {
                _loadedPackagesEvent.FireOnce();
            }
        }
    }
}
