﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
namespace SiliconStudio.Paradox.Graphics
{
    /// <summary>
    /// A GPU resource mapped for CPU access. This is returned by using <see cref="GraphicsDevice.MapSubresource"/>
    /// </summary>
    public struct MappedResource
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MappedResource"/> struct.
        /// </summary>
        /// <param name="resource">The resource.</param>
        /// <param name="subResourceIndex">Index of the sub resource.</param>
        /// <param name="dataBox">The data box.</param>
        internal MappedResource(GraphicsResource resource, int subResourceIndex, DataBox dataBox)
        {
            Resource = resource;
            SubResourceIndex = subResourceIndex;
            DataBox = dataBox;
            OffsetInBytes = 0;
            SizeInBytes = -1;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MappedResource"/> struct.
        /// </summary>
        /// <param name="resource">The resource.</param>
        /// <param name="subResourceIndex">Index of the sub resource.</param>
        /// <param name="dataBox">The data box.</param>
        /// <param name="offsetInBytes">Offset since the beginning of the buffer.</param>
        /// <param name="sizeInBytes">Size of the mapped resource.</param>
        internal MappedResource(GraphicsResource resource, int subResourceIndex, DataBox dataBox, int offsetInBytes, int sizeInBytes)
        {
            Resource = resource;
            SubResourceIndex = subResourceIndex;
            DataBox = dataBox;
            OffsetInBytes = offsetInBytes;
            SizeInBytes = sizeInBytes;
        }

        /// <summary>
        /// The resource mapped.
        /// </summary>
        public readonly GraphicsResource Resource;

        /// <summary>
        /// The subresource index.
        /// </summary>
        public readonly int SubResourceIndex;

        /// <summary>
        /// The data box
        /// </summary>
        public readonly DataBox DataBox;

        /// <summary>
        /// the offset of the mapped resource since the beginning of the buffer
        /// </summary>
        public readonly int OffsetInBytes;

        /// <summary>
        /// the size of the mapped resource
        /// </summary>
        public readonly int SizeInBytes;
    }
}