// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.IO;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.Host;

[ExportWorkspaceServiceFactory(typeof(IMetadataService), ServiceLayer.Default), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class MetadataServiceFactory() : IWorkspaceServiceFactory
{
    public IWorkspaceService CreateService(HostWorkspaceServices workspaceServices)
        => new MetadataService(
            workspaceServices.GetRequiredService<IDocumentationProviderService>(),
            workspaceServices.GetRequiredService<IMetadataReferenceCacheService>());

    private sealed class MetadataService : IMetadataService
    {
        private readonly IDocumentationProviderService _documentationProviderService;
        private readonly IMetadataReferenceCacheService _metadataReferenceCacheService;
        private readonly Func<string, MetadataReferenceProperties, PortableExecutableReference> _createReference;

        public MetadataService(
            IDocumentationProviderService documentationProviderService,
            IMetadataReferenceCacheService metadataReferenceCacheService)
        {
            _documentationProviderService = documentationProviderService;
            _metadataReferenceCacheService = metadataReferenceCacheService;
            _createReference = CreateReference;
        }

        public PortableExecutableReference GetReference(string resolvedPath, MetadataReferenceProperties properties)
            => _metadataReferenceCacheService.GetReference(
                resolvedPath, properties, _createReference);

        private PortableExecutableReference CreateReference(
            string path, MetadataReferenceProperties properties)
        {
            var documentationProvider = _documentationProviderService.GetDocumentationProvider(path);

            try
            {
                // Read references through a mapped view instead of copying the whole image into private memory.
                // Files in the SDK packs and the NuGet package cache never change, so they're mapped in place.
                // Anything else may be rebuilt while referenced, so a snapshot copy is mapped instead and the
                // original stays free to be overwritten.
                if (properties.Kind == MetadataImageKind.Assembly &&
                    (ImmutableReferenceLocations.Contains(path) ? path : ReferenceSnapshots.TryCreate(path)) is { } mappedPath)
                {
                    var module = ModuleMetadata.CreateFromStream(new FileStream(mappedPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                    if (module.GetModuleNames().IsEmpty)
                        return AssemblyMetadata.Create(module).GetReference(documentationProvider, properties.Aliases, properties.EmbedInteropTypes, path);

                    module.Dispose();
                }

                return MetadataReference.CreateFromFile(path, properties, documentationProvider);
            }
            catch (IOException e)
            {
                // Store failed references in the cache so that the behavior stays consistent once we observe the failure.
                return new ThrowingExecutableReference(path, properties, documentationProvider, e);
            }
        }

        /// <summary>
        /// Per-process snapshot copies of references that may change on disk. Each server process owns its own
        /// folder, and folders left behind by processes that have exited are removed the first time it's used.
        /// </summary>
        private static class ReferenceSnapshots
        {
            private static readonly Lazy<string?> s_directory = new(CreateDirectory);
            private static int s_next;

            public static string? TryCreate(string path)
            {
                if (s_directory.Value is not { } directory)
                    return null;

                try
                {
                    var snapshot = Path.Combine(directory, System.Threading.Interlocked.Increment(ref s_next).ToString() + Path.GetExtension(path));
                    File.Copy(path, snapshot, overwrite: true);
                    return snapshot;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            private static string? CreateDirectory()
            {
                try
                {
                    var root = Path.Combine(Path.GetTempPath(), "roslyn-reference-snapshots");
                    Directory.CreateDirectory(root);

                    foreach (var stale in Directory.EnumerateDirectories(root))
                    {
                        if (int.TryParse(Path.GetFileName(stale), out var owner) && IsRunning(owner))
                            continue;

                        try
                        {
                            Directory.Delete(stale, recursive: true);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                        }
                    }

#if NET
                    var directory = Path.Combine(root, Environment.ProcessId.ToString());
#else
                    var directory = Path.Combine(root, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
#endif
                    Directory.CreateDirectory(directory);
                    return directory;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            private static bool IsRunning(int processId)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(processId);
                    return !process.HasExited;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }
        }

        private static class ImmutableReferenceLocations
        {
            private static readonly string[] s_roots = GetRoots();

            public static bool Contains(string path)
            {
                foreach (var root in s_roots)
                {
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            private static string[] GetRoots()
            {
                var roots = new System.Collections.Generic.List<string>();

                var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                if (string.IsNullOrEmpty(packages))
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(profile))
                        packages = Path.Combine(profile, ".nuget", "packages");
                }

                if (!string.IsNullOrEmpty(packages))
                    roots.Add(WithSeparator(packages));

                // The runtime directory is <dotnet>/shared/Microsoft.NETCore.App/<version>/; reference packs live under <dotnet>/packs.
                var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                if (Directory.GetParent(runtime.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.Parent?.Parent is { } dotnet)
                {
                    roots.Add(WithSeparator(Path.Combine(dotnet.FullName, "packs")));
                    roots.Add(WithSeparator(Path.Combine(dotnet.FullName, "shared")));
                }

                var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (!string.IsNullOrEmpty(dotnetRoot))
                {
                    roots.Add(WithSeparator(Path.Combine(dotnetRoot, "packs")));
                    roots.Add(WithSeparator(Path.Combine(dotnetRoot, "shared")));
                }

                return [.. roots];
            }

            private static string WithSeparator(string path)
                => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private sealed class ThrowingExecutableReference(string resolvedPath, MetadataReferenceProperties properties, DocumentationProvider documentationProvider, IOException exception)
            : PortableExecutableReference(properties, resolvedPath)
        {
            protected override DocumentationProvider CreateDocumentationProvider()
                => documentationProvider;

            protected override Metadata GetMetadataImpl()
                => throw exception;

            protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties)
                => new ThrowingExecutableReference(FilePath!, properties, documentationProvider, exception);
        }
    }
}
