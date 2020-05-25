﻿using System;
using AsmResolver.DotNet.Builder.Metadata;
using AsmResolver.DotNet.Code;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE;
using AsmResolver.PE.DotNet;
using AsmResolver.PE.DotNet.Metadata.Blob;
using AsmResolver.PE.DotNet.Metadata.Guid;
using AsmResolver.PE.DotNet.Metadata.Strings;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.PE.DotNet.Metadata.UserStrings;

namespace AsmResolver.DotNet.Builder
{
    /// <summary>
    /// Provides a default implementation of <see cref="IPEImageBuilder"/>.
    /// </summary>
    public class ManagedPEImageBuilder : IPEImageBuilder
    {
        /// <summary>
        /// Gets or sets the flags defining the behaviour of the .NET metadata directory builder.
        /// </summary>
        public MetadataBuilderFlags MetadataBuilderFlags
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the method body serializer to use for constructing method bodies.
        /// </summary>
        public IMethodBodySerializer MethodBodySerializer
        {
            get;
            set;
        } = new CilMethodBodySerializer();

        /// <inheritdoc />
        public IPEImage CreateImage(ModuleDefinition module)
        {
            var image = new PEImage();

            image.DotNetDirectory = CreateDotNetDirectory(image, module);

            return image;
        }

        private IDotNetDirectory CreateDotNetDirectory(PEImage image, ModuleDefinition module)
        {
            var metadataBuffer = CreateMetadataBuffer(module);
            var dotNetDirectoryBuffer = new DotNetDirectoryBuffer(module, MethodBodySerializer, metadataBuffer);
            
            ImportTablesStreamIfSpecified(dotNetDirectoryBuffer, module);
            
            // If module is the manifest module, include the entire assembly.
            if (module.Assembly?.ManifestModule == module)
                dotNetDirectoryBuffer.AddAssembly(module.Assembly);
            else
                dotNetDirectoryBuffer.AddModule(module);

            return dotNetDirectoryBuffer.CreateDirectory();
        }

        private IMetadataBuffer CreateMetadataBuffer(ModuleDefinition module)
        {
            var metadataBuffer = new MetadataBuffer();
            
            // Check if there exists a .NET directory to base off the metadata buffer on.
            var originalMetadata = module.DotNetDirectory?.Metadata;
            if (originalMetadata is null)
                return metadataBuffer;
            
            // Import original contents of the blob stream if specified.
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveBlobIndices) != 0)
                metadataBuffer.BlobStream.ImportStream(originalMetadata.GetStream<BlobStream>());

            // Import original contents of the GUID stream if specified.
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveGuidIndices) != 0)
                metadataBuffer.GuidStream.ImportStream(originalMetadata.GetStream<GuidStream>());

            // Import original contents of the strings stream if specified.
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveStringIndices) != 0)
                metadataBuffer.StringsStream.ImportStream(originalMetadata.GetStream<StringsStream>());

            // Import original contents of the strings stream if specified.
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveUserStringIndices) != 0)
                metadataBuffer.UserStringsStream.ImportStream(originalMetadata.GetStream<UserStringsStream>());

            return metadataBuffer;
        }
        
        private void ImportTablesStreamIfSpecified(DotNetDirectoryBuffer buffer, ModuleDefinition module)
        {
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveTypeReferenceIndices) != 0)
                ImportTableIntoTableBuffers<TypeReference>(module, TableIndex.TypeRef, buffer.GetTypeReferenceToken);
            
            if ((MetadataBuilderFlags & MetadataBuilderFlags.PreserveMemberReferenceIndices) != 0)
                ImportTableIntoTableBuffers<MemberReference>(module, TableIndex.MemberRef, buffer.GetMemberReferenceToken);
        }

        private static void ImportTableIntoTableBuffers<TMember>(ModuleDefinition module, TableIndex tableIndex,
            Func<TMember, MetadataToken> importAction)
        {
            int count = module.DotNetDirectory.Metadata
                .GetStream<TablesStream>()
                .GetTable(tableIndex)
                .Count;

            for (uint rid = 1; rid <= count; rid++)
                importAction((TMember) module.LookupMember(new MetadataToken(tableIndex, rid)));
        }
        
    }
}