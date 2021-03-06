﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SiliconStudio.Assets;
using SiliconStudio.BuildEngine;
using SiliconStudio.Core.Extensions;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Paradox.Rendering.Materials;
using SiliconStudio.Paradox.Rendering;
using SiliconStudio.Paradox.Rendering.Data;
using SiliconStudio.Paradox.Extensions;
using SiliconStudio.Core.Serialization;
using SiliconStudio.Core.Serialization.Assets;
using SiliconStudio.Paradox.Animations;
using SiliconStudio.Paradox.Graphics;
using SiliconStudio.Paradox.Graphics.Data;
using SiliconStudio.Paradox.Shaders;

namespace SiliconStudio.Paradox.Assets.Model
{
    public abstract class ImportModelCommand : SingleFileImportCommand
    {
        private delegate bool SameGroup(Rendering.Model model, Mesh baseMesh, Mesh newMesh);

        /// <inheritdoc/>
        public override IEnumerable<Tuple<string, string>> TagList { get { yield return Tuple.Create("Texture", "Value of the TextureTag property"); } }

        private static int spawnedFbxCommands;
        protected TagSymbol TextureTagSymbol;

        public string TextureTag { get; set; }
        
        public string ExportType { get; set; }
        public bool TessellationAEN { get; set; }
        public string EffectName { get; set; }
        public AnimationRepeatMode AnimationRepeatMode { get; set; }

        public List<ModelMaterial> Materials { get; set; }

        public bool Compact { get; set; }
        public List<string> PreservedNodes { get; set; }

        public bool Allow32BitIndex { get; set; }
        public bool AllowUnsignedBlendIndices { get; set; }

        public float ScaleImport { get; set; }

        protected ImportModelCommand()
        {
            // Set default values
            ExportType = "model";
            TextureTag = "fbx-texture";
            TextureTagSymbol = RegisterTag("Texture", () => TextureTag);
            AnimationRepeatMode = AnimationRepeatMode.LoopInfinite;
            ScaleImport = 1.0f;
        }

        private string ContextAsString
        {
            get
            {
                return string.Format("model [{0}] from import [{1}]", Location, SourcePath);
            }
        }

        /// <summary>
        /// The method to override containing the actual command code. It is called by the <see cref="DoCommand" /> function
        /// </summary>
        /// <param name="commandContext">The command context.</param>
        /// <returns>Task{ResultStatus}.</returns>
        protected override async Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
        {
            var assetManager = new AssetManager();

            while (Interlocked.Increment(ref spawnedFbxCommands) >= 2)
            {
                Interlocked.Decrement(ref spawnedFbxCommands);
                await Task.Delay(1, CancellationToken);
            }

            try
            {
                object exportedObject;

                if (ExportType == "animation")
                {
                    // Read from model file
                    var animationClip = LoadAnimation(commandContext, assetManager);
                    exportedObject = animationClip;
                    if (animationClip == null)
                    {
                        commandContext.Logger.Info("File {0} has an empty animation.", SourcePath);
                    }
                    else if (animationClip.Duration.Ticks == 0)
                    {
                        commandContext.Logger.Warning("File {0} has a 0 tick long animation.", SourcePath);
                    }
                    else
                    {
                        animationClip.RepeatMode = AnimationRepeatMode;
                        animationClip.Optimize();
                    }
                }
                else if (ExportType == "model")
                {
                    // Read from model file
                    var model = LoadModel(commandContext, assetManager);

                    // Apply materials
                    foreach (var modelMaterial in Materials)
                    {
                        if (modelMaterial.MaterialInstance == null || modelMaterial.MaterialInstance.Material == null)
                        {
                            commandContext.Logger.Warning(string.Format("The material [{0}] is null in the list of materials.", modelMaterial.Name));
                            continue;
                        }                       
                        model.Materials.Add(modelMaterial.MaterialInstance);
                    }

                    model.BoundingBox = BoundingBox.Empty;
                    var hierarchyUpdater = new ModelViewHierarchyUpdater(model.Hierarchy.Nodes);
                    hierarchyUpdater.UpdateMatrices();

                    bool hasErrors = false;
                    foreach (var mesh in model.Meshes)
                    {
                        if (TessellationAEN)
                        {
                            // TODO: Generate AEN model view
                            commandContext.Logger.Error("TessellationAEN is not supported in {0}", ContextAsString);
                            hasErrors = true;
                        }
                    }

                    // split the meshes if necessary
                    model.Meshes = SplitExtensions.SplitMeshes(model.Meshes, Allow32BitIndex);

                    // merge the meshes
                    if (Compact)
                    {
                        var indicesBlackList = new HashSet<int>();
                        if (PreservedNodes != null)
                        {
                            for (var index = 0; index < model.Hierarchy.Nodes.Length; ++index)
                            {
                                var node = model.Hierarchy.Nodes[index];
                                if (PreservedNodes.Contains(node.Name))
                                    indicesBlackList.Add(index);
                            }
                        }

                        // group meshes with same material and same root
                        var sameMaterialMeshes = new List<GroupList<int, Mesh>>();
                        GroupFromIndex(model, 0, indicesBlackList, model.Meshes, sameMaterialMeshes);

                        // remove meshes that cannot be merged
                        var excludedMeshes = new List<Mesh>();
                        var finalMeshGroups = new List<GroupList<int, Mesh>>();
                        foreach (var meshList in sameMaterialMeshes)
                        {
                            var mergeList = new GroupList<int, Mesh> { Key = meshList.Key };

                            foreach (var mesh in meshList)
                            {
                                if (mesh.Skinning != null || indicesBlackList.Contains(mesh.NodeIndex))
                                    excludedMeshes.Add(mesh);
                                else
                                    mergeList.Add(mesh);
                            }

                            if (mergeList.Count <= 1)
                                excludedMeshes.AddRange(mergeList);
                            else
                                finalMeshGroups.Add(mergeList);
                        }

                        var finalMeshes = new List<Mesh>();

                        finalMeshes.AddRange(excludedMeshes);

                        foreach (var meshList in finalMeshGroups)
                        {
                            // transform the buffers
                            foreach (var mesh in meshList)
                            {
                                var transformationMatrix = GetMatrixFromIndex(model.Hierarchy.Nodes, hierarchyUpdater, meshList.Key, mesh.NodeIndex);
                                mesh.Draw.VertexBuffers[0].TransformBuffer(ref transformationMatrix);
                            }

                            // refine the groups base on several tests
                            var newMeshGroups = new List<GroupList<int, Mesh>> { meshList };
                            // only regroup meshes if they share the same parameters
                            newMeshGroups = RefineGroups(model, newMeshGroups, CompareParameters);

                            // only regroup meshes if they share the shadow options
                            newMeshGroups = RefineGroups(model, newMeshGroups, CompareShadowOptions);

                            // add to the final meshes groups
                            foreach (var sameParamsMeshes in newMeshGroups)
                            {
                                var baseMesh = sameParamsMeshes[0];
                                var newMeshList = sameParamsMeshes.Select(x => x.Draw).ToList().GroupDrawData(Allow32BitIndex);
                                foreach (var generatedMesh in newMeshList)
                                {
                                    finalMeshes.Add(new Mesh(generatedMesh, baseMesh.Parameters) {
                                            MaterialIndex = baseMesh.MaterialIndex,
                                            Name = baseMesh.Name,
                                            Draw = generatedMesh,
                                            NodeIndex = meshList.Key,
                                            Skinning = null,
                                        });
                                }
                            }
                        }

                        // delete empty nodes (neither mesh nor bone attached)
                        var keptNodes = new bool[model.Hierarchy.Nodes.Length];
                        for (var i = 0; i < keptNodes.Length; ++i)
                        {
                            keptNodes[i] = false;
                        }
                        foreach (var keepIndex in indicesBlackList)
                        {
                            var nodeIndex = keepIndex;
                            while (nodeIndex != -1 && !keptNodes[nodeIndex])
                            {
                                keptNodes[nodeIndex] = true;
                                nodeIndex = model.Hierarchy.Nodes[nodeIndex].ParentIndex;
                            }
                        }
                        foreach (var mesh in finalMeshes)
                        {
                            var nodeIndex = mesh.NodeIndex;
                            while (nodeIndex != -1 && !keptNodes[nodeIndex])
                            {
                                keptNodes[nodeIndex] = true;
                                nodeIndex = model.Hierarchy.Nodes[nodeIndex].ParentIndex;
                            }

                            if (mesh.Skinning != null)
                            {
                                foreach (var bone in mesh.Skinning.Bones)
                                {
                                    nodeIndex = bone.NodeIndex;
                                    while (nodeIndex != -1 && !keptNodes[nodeIndex])
                                    {
                                        keptNodes[nodeIndex] = true;
                                        nodeIndex = model.Hierarchy.Nodes[nodeIndex].ParentIndex;
                                    }
                                }
                            }
                        }

                        var newNodes = new List<ModelNodeDefinition>();
                        var newMapping = new int[model.Hierarchy.Nodes.Length];
                        for (var i = 0; i < keptNodes.Length; ++i)
                        {
                            if (keptNodes[i])
                            {
                                var parentIndex = model.Hierarchy.Nodes[i].ParentIndex;
                                if (parentIndex != -1)
                                    model.Hierarchy.Nodes[i].ParentIndex = newMapping[parentIndex]; // assume that the nodes are well ordered
                                newMapping[i] = newNodes.Count;
                                newNodes.Add(model.Hierarchy.Nodes[i]);
                            }
                        }

                        foreach (var mesh in finalMeshes)
                        {
                            mesh.NodeIndex = newMapping[mesh.NodeIndex];
                            
                            if (mesh.Skinning != null)
                            {
                                for (var i = 0; i < mesh.Skinning.Bones.Length; ++i)
                                {
                                    mesh.Skinning.Bones[i].NodeIndex = newMapping[mesh.Skinning.Bones[i].NodeIndex];
                                }
                            }
                        }

                        model.Meshes = finalMeshes;
                        model.Hierarchy.Nodes = newNodes.ToArray();

                        hierarchyUpdater = new ModelViewHierarchyUpdater(model.Hierarchy.Nodes);
                        hierarchyUpdater.UpdateMatrices();
                    }

                    // bounding boxes
                    var modelBoundingBox = model.BoundingBox;
                    var modelBoundingSphere = model.BoundingSphere;
                    foreach (var mesh in model.Meshes)
                    {
                        var vertexBuffers = mesh.Draw.VertexBuffers;
                        if (vertexBuffers.Length > 0)
                        {
                            // Compute local mesh bounding box (no node transformation)
                            Matrix matrix = Matrix.Identity;
                            mesh.BoundingBox = vertexBuffers[0].ComputeBounds(ref matrix, out mesh.BoundingSphere);

                            // Compute model bounding box (includes node transformation)
                            hierarchyUpdater.GetWorldMatrix(mesh.NodeIndex, out matrix);
                            BoundingSphere meshBoundingSphere;
                            var meshBoundingBox = vertexBuffers[0].ComputeBounds(ref matrix, out meshBoundingSphere);
                            BoundingBox.Merge(ref modelBoundingBox, ref meshBoundingBox, out modelBoundingBox);
                            BoundingSphere.Merge(ref modelBoundingSphere, ref meshBoundingSphere, out modelBoundingSphere);
                        }

                        // TODO: temporary Always try to compact
                        mesh.Draw.CompactIndexBuffer();
                    }
                    model.BoundingBox = modelBoundingBox;
                    model.BoundingSphere = modelBoundingSphere;

                    // merges all the Draw VB and IB together to produce one final VB and IB by entity.
                    var sizeVertexBuffer = model.Meshes.SelectMany(x => x.Draw.VertexBuffers).Select(x => x.Buffer.GetSerializationData().Content.Length).Sum();
                    var sizeIndexBuffer = 0;
                    foreach (var x in model.Meshes)
                    {
                        // Let's be aligned (if there was 16bit indices before, we might be off)
                        if (x.Draw.IndexBuffer.Is32Bit && sizeIndexBuffer % 4 != 0)
                            sizeIndexBuffer += 2;

                        sizeIndexBuffer += x.Draw.IndexBuffer.Buffer.GetSerializationData().Content.Length;
                    }
                    var vertexBuffer = new BufferData(BufferFlags.VertexBuffer, new byte[sizeVertexBuffer]);
                    var indexBuffer = new BufferData(BufferFlags.IndexBuffer, new byte[sizeIndexBuffer]);

                    // Note: reusing same instance, to avoid having many VB with same hash but different URL
                    var vertexBufferSerializable = vertexBuffer.ToSerializableVersion();
                    var indexBufferSerializable = indexBuffer.ToSerializableVersion();

                    var vertexBufferNextIndex = 0;
                    var indexBufferNextIndex = 0;
                    foreach (var drawMesh in model.Meshes.Select(x => x.Draw))
                    {
                        // the index buffer
                        var oldIndexBuffer = drawMesh.IndexBuffer.Buffer.GetSerializationData().Content;

                        // Let's be aligned (if there was 16bit indices before, we might be off)
                        if (drawMesh.IndexBuffer.Is32Bit && indexBufferNextIndex % 4 != 0)
                            indexBufferNextIndex += 2;

                        Array.Copy(oldIndexBuffer, 0, indexBuffer.Content, indexBufferNextIndex, oldIndexBuffer.Length);
                    
                        drawMesh.IndexBuffer = new IndexBufferBinding(indexBufferSerializable, drawMesh.IndexBuffer.Is32Bit, drawMesh.IndexBuffer.Count, indexBufferNextIndex);
                    
                        indexBufferNextIndex += oldIndexBuffer.Length;
                    
                        // the vertex buffers
                        for (int index = 0; index < drawMesh.VertexBuffers.Length; index++)
                        {
                            var vertexBufferBinding = drawMesh.VertexBuffers[index];
                            var oldVertexBuffer = vertexBufferBinding.Buffer.GetSerializationData().Content;

                            Array.Copy(oldVertexBuffer, 0, vertexBuffer.Content, vertexBufferNextIndex, oldVertexBuffer.Length);

                            drawMesh.VertexBuffers[index] = new VertexBufferBinding(vertexBufferSerializable, vertexBufferBinding.Declaration, vertexBufferBinding.Count, vertexBufferBinding.Stride, vertexBufferNextIndex);

                            vertexBufferNextIndex += oldVertexBuffer.Length;
                        }
                    }


                    // If there were any errors while importing models
                    if (hasErrors)
                    {
                        return ResultStatus.Failed;
                    }

                    // Convert to Entity
                    exportedObject = model;
                }
                else
                {
                    commandContext.Logger.Error("Unknown export type [{0}] {1}", ExportType, ContextAsString);
                    return ResultStatus.Failed;
                }

                if (exportedObject != null)
                    assetManager.Save(Location, exportedObject);

                commandContext.Logger.Info("The {0} has been successfully imported.", ContextAsString);

                return ResultStatus.Successful;
            }
            catch (Exception ex)
            {
                commandContext.Logger.Error("Unexpected error while importing {0}", ex, ContextAsString);
                return ResultStatus.Failed;
            }
            finally
            {
                Interlocked.Decrement(ref spawnedFbxCommands);
            }
        }

        /// <summary>
        /// Refine the mesh groups based on the result of the delegate test method.
        /// </summary>
        /// <param name="meshList">The list of mesh groups.</param>
        /// <param name="sameGroupDelegate">The test delegate.</param>
        /// <returns>The new list of mesh groups.</returns>
        private List<GroupList<int, Mesh>> RefineGroups(Rendering.Model model, List<GroupList<int, Mesh>> meshList, SameGroup sameGroupDelegate)
        {
            var finalGroups = new List<GroupList<int, Mesh>>();
            foreach (var meshGroup in meshList)
            {
                var updatedGroups = new List<GroupList<int, Mesh>>();
                foreach (var mesh in meshGroup)
                {
                    var createNewGroup = true;
                    foreach (var sameParamsMeshes in updatedGroups)
                    {
                        if (sameGroupDelegate(model, sameParamsMeshes[0], mesh))
                        {
                            sameParamsMeshes.Add(mesh);
                            createNewGroup = false;
                            break;
                        }
                    }

                    if (createNewGroup)
                    {
                        var newGroup = new GroupList<int, Mesh> { Key = meshGroup.Key };
                        newGroup.Add(mesh);
                        updatedGroups.Add(newGroup);
                    }
                }
                finalGroups.AddRange(updatedGroups);
            }
            return finalGroups;
        }

        /// <summary>
        /// Create groups of mergeable meshes.
        /// </summary>
        /// <param name="model">The current model.</param>
        /// <param name="index">The index of the currently visited node.</param>
        /// <param name="nodeBlackList">List of the nodes that should be kept.</param>
        /// <param name="meshes">The meshes and their node index.</param>
        /// <param name="finalLists">List of mergeable meshes and their root node.</param>
        /// <returns>A list of mergeable meshes in progress.</returns>
        private Dictionary<int, List<Mesh>> GroupFromIndex(Rendering.Model model, int index, HashSet<int> nodeBlackList, List<Mesh> meshes, List<GroupList<int, Mesh>> finalLists)
        {
            var children = GetChildren(model.Hierarchy.Nodes, index);
            
            var materialGroups = new Dictionary<int, List<Mesh>>();

            // Get the group from each child
            foreach (var child in children)
            {
                var newMaterialGroups = GroupFromIndex(model, child, nodeBlackList, meshes, finalLists);

                foreach (var group in newMaterialGroups)
                {
                    if (!materialGroups.ContainsKey(group.Key))
                        materialGroups.Add(group.Key, new List<Mesh>());
                    materialGroups[group.Key].AddRange(group.Value);
                }
            }

            // Add the current node if it has a mesh
            foreach (var nodeMesh in meshes.Where(x => x.NodeIndex == index))
            {
                var matId = nodeMesh.MaterialIndex;
                if (!materialGroups.ContainsKey(matId))
                    materialGroups.Add(matId, new List<Mesh>());
                materialGroups[matId].Add(nodeMesh);
            }

            // Store the generated list as final if the node should be kept
            if (nodeBlackList.Contains(index) || index == 0)
            {
                foreach (var materialGroup in materialGroups)
                {
                    var groupList = new GroupList<int, Mesh>();
                    groupList.Key = index;
                    groupList.AddRange(materialGroup.Value);
                    finalLists.Add(groupList);
                }
                materialGroups.Clear();
            }

            return materialGroups;
        }

        /// <summary>
        /// Get the transformation matrix to go from rootIndex to index.
        /// </summary>
        /// <param name="hierarchy">The node hierarchy.</param>
        /// <param name="updater">The updater containing the local matrices.</param>
        /// <param name="rootIndex">The root index.</param>
        /// <param name="index">The current index.</param>
        /// <returns>The matrix at this index.</returns>
        private Matrix GetMatrixFromIndex(ModelNodeDefinition[] hierarchy, ModelViewHierarchyUpdater updater, int rootIndex, int index)
        {
            if (index == -1 || index == rootIndex)
                return Matrix.Identity;

            Matrix outMatrix;
            updater.GetLocalMatrix(index, out outMatrix);

            if (index != rootIndex)
            {
                var topMatrix = GetMatrixFromIndex(hierarchy, updater, rootIndex, hierarchy[index].ParentIndex);
                outMatrix = Matrix.Multiply(outMatrix, topMatrix);
            }

            return outMatrix;
        }
        
        /// <summary>
        /// Get the children of the requested node.
        /// </summary>
        /// <param name="hierarchy">The node hierarchy.</param>
        /// <param name="index">The current node index.</param>
        /// <returns>A list of all the indices of the children.</returns>
        private List<int> GetChildren(ModelNodeDefinition[] hierarchy, int index)
        {
            var result = new List<int>();
            for (var i = 0; i < hierarchy.Length; ++i)
            {
                if (hierarchy[i].ParentIndex == index)
                    result.Add(i);
            }
            return result;
        }

        protected abstract Rendering.Model LoadModel(ICommandContext commandContext, AssetManager assetManager);

        protected abstract AnimationClip LoadAnimation(ICommandContext commandContext, AssetManager assetManager);

        protected override void ComputeParameterHash(BinarySerializationWriter writer)
        {
            base.ComputeParameterHash(writer);

            writer.SerializeExtended(this, ArchiveMode.Serialize);
        }

        public override IEnumerable<ObjectUrl> GetInputFiles()
        {
            yield return new ObjectUrl(UrlType.File, SourcePath);
        }

        /// <summary>
        /// Parses a shader source definition
        /// </summary>
        /// <param name="shaderSource">The shader source.</param>
        /// <returns>an instance of ShaderSource.</returns>
        protected static ShaderSource ParseShaderSource(string shaderSource)
        {
            var sources = shaderSource.Split(new[] { ",", ";" }, StringSplitOptions.RemoveEmptyEntries);

            if (sources.Length == 1)
            {
                return new ShaderClassSource(sources[0]);
            }

            var mixin = new ShaderMixinSource();
            foreach (var source in sources)
            {
                mixin.Mixins.Add(new ShaderClassSource(source));
            }

            return mixin;
        }

        /// <summary>
        /// Compares the parameters of the two meshes.
        /// </summary>
        /// <param name="baseMesh">The base mesh.</param>
        /// <param name="newMesh">The mesh to compare.</param>
        /// <param name="extra">Unused parameter.</param>
        /// <returns>True if all the parameters are the same, false otherwise.</returns>
        private static bool CompareParameters(Rendering.Model model, Mesh baseMesh, Mesh newMesh)
        {
            var localParams = baseMesh.Parameters;
            if (localParams == null && newMesh.Parameters == null)
                return true;
            if (localParams == null || newMesh.Parameters == null)
                return false;
            return AreCollectionsEqual(localParams, newMesh.Parameters);
        }
        
        /// <summary>
        /// Compares the shadow options between the two meshes.
        /// </summary>
        /// <param name="baseMesh">The base mesh.</param>
        /// <param name="newMesh">The mesh to compare.</param>
        /// <param name="extra">Unused parameter.</param>
        /// <returns>True if the options are the same, false otherwise.</returns>
        private static bool CompareShadowOptions(Rendering.Model model, Mesh baseMesh, Mesh newMesh)
        {
            // TODO: Check is Model the same for the two mesh?
            var material1 = model.Materials.GetItemOrNull(baseMesh.MaterialIndex);
            var material2 = model.Materials.GetItemOrNull(newMesh.MaterialIndex);

            return material1 == material2 || (material1 != null && material2 != null && material1.IsShadowCaster == material2.IsShadowCaster &&
                material1.IsShadowReceiver == material2.IsShadowReceiver);
        }

        /// <summary>
        /// Compares the value behind a key in two ParameterCollection.
        /// </summary>
        /// <param name="parameters0">The first ParameterCollection.</param>
        /// <param name="parameters1">The second ParameterCollection.</param>
        /// <param name="key">The ParameterKey.</param>
        /// <returns>True</returns>
        private static bool CompareKeyValue<T>(ParameterCollection parameters0, ParameterCollection parameters1, ParameterKey<T> key)
        {
            var value0 = parameters0 != null && parameters0.ContainsKey(key) ? parameters0[key] : key.DefaultValueMetadataT.DefaultValue;
            var value1 = parameters1 != null && parameters1.ContainsKey(key) ? parameters1[key] : key.DefaultValueMetadataT.DefaultValue;
            return value0 == value1;
        }

        /// <summary>
        /// Test if two ParameterCollection are equal
        /// </summary>
        /// <param name="parameters0">The first ParameterCollection.</param>
        /// <param name="parameters1">The second ParameterCollection.</param>
        /// <returns>True if the collections are the same, false otherwise.</returns>
        private static bool AreCollectionsEqual(ParameterCollection parameters0, ParameterCollection parameters1)
        {
            bool result = true;
            foreach (var paramKey in parameters0)
            {
                result &= parameters1.ContainsKey(paramKey.Key) && parameters1[paramKey.Key].Equals(paramKey.Value);
            }
            foreach (var paramKey in parameters1)
            {
                result &= parameters0.ContainsKey(paramKey.Key) && parameters0[paramKey.Key].Equals(paramKey.Value);
            }
            return result;
        }

        public override string ToString()
        {
            return (SourcePath ?? "[File]") + (ExportType != null ? " (" + ExportType + ")" : "") + " > " + (Location ?? "[Location]");
        }

    }

    /// <summary>
    /// A implementation of IGrouping.
    /// </summary>
    /// <typeparam name="TK">Key type.</typeparam>
    /// <typeparam name="T">Element type.</typeparam>
    public class GroupList<TK,T> : List<T>, IGrouping<TK,T>
    {
        public TK Key { get; set; }
    }
}