﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AsmResolver.Net.Emit;
using AsmResolver.Net.Cts;
using AsmResolver.Net.Metadata;

namespace AsmResolver.Net
{
    /// <summary>
    /// Represents the header to the .NET metadata in a windows assembly image.
    /// </summary>
    public class MetadataHeader : FileSegment
    {
        internal static MetadataHeader FromReadingContext(ReadingContext context)
        {
            var reader = context.Reader;

            var header = new MetadataHeader
            {
                StartOffset = reader.Position,

                Signature = reader.ReadUInt32(),
                MajorVersion = reader.ReadUInt16(),
                MinorVersion = reader.ReadUInt16(),
                Reserved = reader.ReadUInt32(),
                VersionLength = reader.ReadUInt32(),
            };

            header.VersionString = Encoding.ASCII.GetString(reader.ReadBytes((int)header.VersionLength));
            header.Flags = reader.ReadUInt16();
            var streamCount = reader.ReadUInt16();

            for (int i = 0; i < streamCount; i++)
            {
                var streamHeader = MetadataStreamHeader.FromReadingContext(context);
                header.StreamHeaders.Add(streamHeader);
            }

            return header;
        }

        private MetadataHeader()
        {
            StreamHeaders = new MetadataStreamHeaderCollection(this);
        }

        public MetadataHeader(ImageNetDirectory directory)
            : this()
        {
            NetDirectory = directory;
        }

        /// <summary>
        /// Gets the parent .NET data directory.
        /// </summary>
        public ImageNetDirectory NetDirectory
        {
            get;
            internal set;
        }

        /// <summary>
        /// Gets or sets the signature of the metadata header. Must be 0x424A5342 (BSJB) for a valid .NET executable.
        /// </summary>
        public uint Signature
        {
            get;
            set;
        }

        public ushort MajorVersion
        {
            get;
            set;
        }

        public ushort MinorVersion
        {
            get;
            set;
        }

        /// <summary>
        /// Reserved. Must be zero.
        /// </summary>
        public uint Reserved
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the length of the <see cref="VersionString"/> field.
        /// </summary>
        public uint VersionLength
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the version of the .NET framework that the .net assembly image targets.
        /// </summary>
        public string VersionString
        {
            get;
            set;
        }

        /// <summary>
        /// Reserved. Should be zero.
        /// </summary>
        public ushort Flags
        {
            get;
            set;
        }

        /// <summary>
        /// Gets a list of all stream headers defined in the metadata data directory.
        /// </summary>
        public IList<MetadataStreamHeader> StreamHeaders
        {
            get;
        }

        public MetadataImage Image
        {
            get;
            private set;
        }

        public bool IsLocked => Image != null;

        /// <summary>
        /// Gets or sets the reader to use in parsing the metadata streams.
        /// </summary>
        public IMetadataStreamParser StreamParser
        {
            get;
            set;
        } = new DefaultMetadataStreamParser();
        
        /// <summary>
        /// Gets all metadata heap streams defined in the metadata data directory.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<MetadataStream> GetStreams()
        {
            return StreamHeaders.Select(x => x.Stream);
        } 

        /// <summary>
        /// Gets the first occuring metadata heap stream with the given name.
        /// </summary>
        /// <param name="name">The name of the stream to get.</param>
        /// <returns></returns>
        public MetadataStream GetStream(string name)
        {
            var header = StreamHeaders.FirstOrDefault(x => x.Name == name);
            return header?.Stream;
        }

        /// <summary>
        /// Gets the first occuring metadata heap stream that inherits from the given type argument.
        /// </summary>
        /// <typeparam name="TStream">The type of the metadata stream.</typeparam>
        /// <returns></returns>
        public TStream GetStream<TStream>()
            where TStream : MetadataStream
        {
            var header = StreamHeaders.FirstOrDefault(x => x.Stream is TStream);
            return (TStream) header?.Stream;
        }

        public MetadataImage LockMetadata()
        {
            if (IsLocked)
                throw new InvalidOperationException("Cannot lock the metadata after the metadata has already been locked.");

            var tableStream = GetStream<TableStream>();
            tableStream.IsReadOnly = true;
            Image = new MetadataImage(this);
            return Image;
        }

        public IDictionary<IMetadataMember, MetadataToken> UnlockMetadata()
        {
            return UnlockMetadata(new DefaultMetadataBuilder());
        }
        
        public IDictionary<IMetadataMember, MetadataToken> UnlockMetadata(IMetadataBuilder builder)
        {
            if (!IsLocked)
                throw new InvalidOperationException("Cannot unlock the metadata if it has not already been locked.");
            
            var image = Image;
            
            // Construct new metadata streams.
            var buffer = builder.Rebuild(image);
            
            // Create resources.
            NetDirectory.ResourcesManifest = buffer.ResourcesBuffer.CreateDirectory();
            
            // Serialize new streams.
            var newStreams = new MetadataStreamBuffer[]
            {
                buffer.TableStreamBuffer,
                buffer.BlobStreamBuffer,
                buffer.GuidStreamBuffer,
                buffer.StringStreamBuffer,
                buffer.UserStringStreamBuffer
            }.ToDictionary(x => x, x => x.CreateStream());

            // Determine new entrypoint token.
            var newTokenMapping = buffer.TableStreamBuffer.GetNewTokenMapping();
            uint entrypointToken;
            if (image.ManagedEntrypoint == null)
            {
                entrypointToken = 0u;
            }
            else
            {
                if (newTokenMapping.TryGetValue(image.ManagedEntrypoint, out var token))
                    entrypointToken = token.ToUInt32();
                else
                    throw new MemberNotImportedException(image.ManagedEntrypoint);
            }

            // Unlock metadata, commit changes to streams.
            Image = null;
            foreach (var entry in newStreams)
            {
                var header = StreamHeaders.FirstOrDefault(x => x.Name == entry.Key.Name);

                if (header == null)
                {
                    header = new MetadataStreamHeader(entry.Key.Name);
                    StreamHeaders.Add(header);
                }

                header.Stream = entry.Value;
            }

            // Update managed entrypoint.
            NetDirectory.EntryPointToken = entrypointToken;
            return newTokenMapping;
            
        }

        public override uint GetPhysicalLength()
        {
            return (uint)(1 * sizeof (uint) +
                          2 * sizeof (ushort) +
                          2 * sizeof (uint) +
                          VersionLength +
                          2 * sizeof (ushort) +
                          StreamHeaders.Sum(x => x.GetPhysicalLength()));
        }

        public override void Write(WritingContext context)
        {
            var writer = context.Writer;

            writer.WriteUInt32(Signature);
            writer.WriteUInt16(MajorVersion);
            writer.WriteUInt16(MinorVersion);
            writer.WriteUInt32(Reserved);
            writer.WriteUInt32(VersionLength);
            writer.WriteAsciiString(VersionString);
            writer.WriteZeroes((int)(VersionLength - VersionString.Length));
            writer.WriteUInt16(Flags);
            writer.WriteUInt16((ushort)StreamHeaders.Count);

            foreach (var streamHeader in StreamHeaders)
                streamHeader.Write(context);
        }

    }
}