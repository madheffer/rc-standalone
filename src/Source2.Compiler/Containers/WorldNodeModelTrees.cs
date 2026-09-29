using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;

namespace Source2.Compiler;

/// <summary>
/// The four trees of a world node model, from a <see cref="WorldNodeModel"/>.
///
/// <para>Key order and value types are Valve's, as the 401 node models of
/// probe01, cardtest and atixref store them. Integers 0 and 1 are written
/// with KV3's no-payload codes (they read back as Int64); any other integer
/// keeps its field's type, Int32 or UInt32. Scalar floats are doubles, vectors
/// are float arrays, except the model info's vectors, which are doubles.</para>
/// </summary>
public static class WorldNodeModelTrees
{
    /// <summary>The CRenderMesh (MDAT).</summary>
    public static KVObject Mdat(WorldNodeModel model)
    {
        var root = KVObject.Collection();
        root.Add("_class", new KVObject("CRenderMesh"));
        var sceneObjects = KVObject.Array();
        foreach (var so in model.SceneObjects)
        {
            var o = KVObject.Collection();
            o.Add("m_vMinBounds", Floats(so.Min));
            o.Add("m_vMaxBounds", Floats(so.Max));
            var draws = KVObject.Array();
            foreach (var d in so.Draws)
                draws.Add(Draw(d));
            o.Add("m_drawCalls", draws);
            var bounds = KVObject.Array();
            foreach (var (min, max) in so.DrawBounds)
            {
                var b = KVObject.Collection();
                b.Add("m_vMinBounds", Floats(min));
                b.Add("m_vMaxBounds", Floats(max));
                bounds.Add(b);
            }
            o.Add("m_drawBounds", bounds);
            var meshlets = KVObject.Array();
            foreach (var m in so.Meshlets)
                meshlets.Add(Meshlet(m));
            o.Add("m_meshlets", meshlets);
            o.Add("m_rtProxyDrawCalls", KVObject.Array());
            o.Add("m_vTintColor", Floats(0f, 0f, 0f, 0f));
            sceneObjects.Add(o);
        }
        root.Add("m_sceneObjects", sceneObjects);
        root.Add("m_constraints", KVObject.Array());
        var skeleton = KVObject.Collection();
        skeleton.Add("m_bones", KVObject.Array());
        skeleton.Add("m_boneParents", KVObject.Array());
        skeleton.Add("m_nBoneWeightCount", I32(4));
        root.Add("m_skeleton", skeleton);
        root.Add("m_bUseUV2ForCharting", new KVObject(false));
        root.Add("m_bEmbeddedMapMesh", new KVObject(true));
        var deform = KVObject.Collection();
        deform.Add("m_flTensionCompressScale", new KVObject(0.0));
        deform.Add("m_flTensionStretchScale", new KVObject(0.0));
        deform.Add("m_bRecomputeSmoothNormalsAfterAnimation", new KVObject(false));
        deform.Add("m_bComputeDynamicMeshTensionAfterAnimation", new KVObject(false));
        deform.Add("m_bSmoothNormalsAcrossUvSeams", new KVObject(false));
        deform.Add("m_bEnableEyeBulgeDeformation", new KVObject(false));
        root.Add("m_meshDeformParams", deform);
        root.Add("m_pGroomData", KVObject.Null());
        root.Add("m_attachments", KVObject.Array());
        root.Add("m_hitboxsets", KVObject.Array());
        root.Add("m_morphSet", new KVObject("") { Flag = KVFlag.Resource });
        return root;
    }

    private static KVObject Draw(WorldNodeModel.Draw d)
    {
        var o = KVObject.Collection();
        o.Add("m_flUvDensity", new KVObject((double)d.UvDensity));
        o.Add("m_vTintColor", Floats(d.Tint.X, d.Tint.Y, d.Tint.Z));
        o.Add("m_flAlpha", new KVObject(1.0));
        o.Add("m_nNumMeshlets", U32(d.MeshletCount));
        o.Add("m_nFirstMeshlet", U32(d.FirstMeshlet));
        o.Add("m_nAppliedIndexOffset", U32(d.AppliedIndexOffset));
        o.Add("m_nEmissivePrimitiveCount", I32(-1));
        o.Add("m_nDepthVertexBufferIndex", U32(d.DepthVertexBuffer));
        o.Add("m_nMeshletPackedIVBIndex", U32(255));
        o.Add("m_rigidMeshParts", KVObject.Array());
        o.Add("m_rootBvhNodes", KVObject.Array());
        o.Add("m_nPrimitiveType", new KVObject("RENDER_PRIM_TRIANGLES"));
        o.Add("m_nBaseVertex", I32(0));
        o.Add("m_nVertexCount", I32(d.VertexEnd));
        o.Add("m_nStartIndex", I32(d.StartIndex));
        o.Add("m_nIndexCount", I32(d.IndexCount));
        o.Add("m_indexBuffer", Binding(d.IndexBufferHandle));
        o.Add("m_meshletPackedIVB", Binding(0));
        o.Add("m_material", new KVObject(d.Material) { Flag = KVFlag.Resource });
        var vbs = KVObject.Array();
        foreach (var h in d.VertexBufferHandles)
            vbs.Add(Binding(h));
        o.Add("m_vertexBuffers", vbs);
        o.Add("m_bUseCompressedNormalTangent", new KVObject(true));
        if (d.BakedLightingFromVertexStream)
            o.Add("m_bHasBakedLightingFromVertexStream", new KVObject(true));
        if (d.NotMatchedToMaterial)
            o.Add("m_bIsNotMatchedToMaterial", new KVObject(true));
        return o;
    }

    private static KVObject Meshlet(WorldNodeModel.Meshlet m)
    {
        var o = KVObject.Collection();
        var aabb = KVObject.Collection();
        aabb.Add("m_nMin", U32(m.PackedMin));
        aabb.Add("m_nMax", U32(m.PackedMax));
        o.Add("m_PackedAABB", aabb);
        var cull = KVObject.Collection();
        var axis = KVObject.Array();
        axis.Add(new KVObject(m.ConeX));
        axis.Add(new KVObject(m.ConeY));
        axis.Add(new KVObject(m.ConeZ));
        cull.Add("m_ConeAxis", axis);
        cull.Add("m_ConeCutoff", I32(m.ConeCutoff));
        o.Add("m_CullingData", cull);
        o.Add("m_nVertexOffset", U32(m.VertexOffset));
        o.Add("m_nTriangleOffset", U32(m.TriangleOffset));
        o.Add("m_nVertexCount", U32(m.VertexCount));
        o.Add("m_nTriangleCount", U32(m.TriangleCount));
        o.Add("m_nBoneIndex", U32(65534));
        return o;
    }

    private static KVObject Binding(int handle)
    {
        var o = KVObject.Collection();
        o.Add("m_hBuffer", U32(handle));
        o.Add("m_nBindOffsetBytes", I32(0));
        return o;
    }

    /// <summary>
    /// The embedded mesh (CTRL). MDAT is the block after the buffers.
    /// </summary>
    public static KVObject Ctrl(WorldNodeModel model)
    {
        var (vbBlocks, ibBlocks) = BufferBlocks(model);
        var mesh = KVObject.Collection();
        mesh.Add("m_Name", new KVObject("meshset_0"));
        mesh.Add("m_nMeshIndex", I32(0));
        mesh.Add("m_nDataBlock", I32(model.VertexBuffers.Count + model.IndexBuffers.Count));
        mesh.Add("m_nMorphBlock", I32(-1));
        var vbs = KVObject.Array();
        var vbAt = 0;
        foreach (var vb in model.VertexBuffers)
        {
            var layout = KVObject.Array();
            foreach (var f in vb.Layout)
            {
                var o = KVObject.Collection();
                o.Add("m_pSemanticName", new KVObject(f.Semantic));
                o.Add("m_nSemanticIndex", I32(f.SemanticIndex));
                o.Add("m_Format", U32(f.Format));
                o.Add("m_nOffset", I32(f.Offset));
                o.Add("m_nSlot", I32(0));
                o.Add("m_nSlotType", new KVObject("RENDER_SLOT_PER_VERTEX"));
                o.Add("m_szShaderSemantic", new KVObject(f.ShaderSemantic));
                layout.Add(o);
            }
            vbs.Add(Buffer(vbBlocks[vbAt++], vb.Count, vb.Stride, vb.Meshopt, false, I32(1), layout));
        }
        mesh.Add("m_vertexBuffers", vbs);
        var ibs = KVObject.Array();
        var ibAt = 0;
        foreach (var ib in model.IndexBuffers)
            ibs.Add(Buffer(ibBlocks[ibAt++], ib.Count, ib.ElementSize, ib.Meshopt, ib.Pooled, U32(2), KVObject.Array()));
        mesh.Add("m_indexBuffers", ibs);
        mesh.Add("m_toolsBuffers", KVObject.Array());
        mesh.Add("m_nVBIBBlock", I32(-1));
        mesh.Add("m_nToolsVBBlock", I32(-1));
        var meshes = KVObject.Array();
        meshes.Add(mesh);
        var root = KVObject.Collection();
        root.Add("embedded_meshes", meshes);
        return root;
    }

    /// <summary>
    /// Each buffer's block index: per index buffer, the vertex buffers its draws
    /// read (first use first), then the index buffer itself (atixref's
    /// blocklight models: VB 0, IB 0, VB 1, IB 1). This is also the order the
    /// buffer blocks are written in.
    /// </summary>
    public static (int[] Vertex, int[] Index) BufferBlocks(WorldNodeModel model)
    {
        var vb = Enumerable.Repeat(-1, model.VertexBuffers.Count).ToArray();
        var ib = new int[model.IndexBuffers.Count];
        var block = 0;
        for (var k = 0; k < ib.Length; k++)
        {
            foreach (var d in model.SceneObjects.SelectMany(so => so.Draws).Where(d => d.IndexBufferHandle == k))
                foreach (var h in d.VertexBufferHandles)
                    if (vb[h] < 0)
                        vb[h] = block++;
            ib[k] = block++;
        }
        for (var h = 0; h < vb.Length; h++)
            if (vb[h] < 0)
                vb[h] = block++;
        return (vb, ib);
    }

    private static KVObject Buffer(int block, int count, int size, bool meshopt, bool pooled, KVObject usage, KVObject layout)
    {
        var o = KVObject.Collection();
        o.Add("m_nBlockIndex", I32(block));
        o.Add("m_nElementCount", U32(count));
        o.Add("m_nElementSizeInBytes", U32(size));
        o.Add("m_bMeshoptCompressed", new KVObject(meshopt));
        o.Add("m_bMeshoptIndexSequence", new KVObject(false));
        o.Add("m_nMeshoptMeshletEncodeVersion", I32(-1));
        o.Add("m_bCompressedZSTD", new KVObject(false));
        o.Add("m_bCreateBufferSRV", new KVObject(false));
        o.Add("m_bCreateBufferUAV", new KVObject(false));
        o.Add("m_bCreateRawBuffer", new KVObject(false));
        o.Add("m_bCreatePooledBuffer", new KVObject(pooled));
        o.Add("m_nBufferUsage", usage);
        o.Add("m_inputLayoutFields", layout);
        return o;
    }

    /// <summary>The edit info (RED2): the compile arguments and the searchable counts.</summary>
    public static KVObject Red2(WorldNodeModel model)
    {
        var root = KVObject.Collection();
        root.Add("m_InputDependencies", KVObject.Array());
        root.Add("m_AdditionalInputDependencies", KVObject.Array());
        var args = KVObject.Array();
        foreach (var (name, type, fallback) in WorldNodeModel.ArgumentList)
        {
            var a = KVObject.Collection();
            a.Add("m_ParameterName", new KVObject(name));
            a.Add("m_ParameterType", new KVObject(type));
            a.Add("m_nFingerprint", U32(model.Argument(name, fallback)));
            a.Add("m_nFingerprintDefault", U32(fallback));
            args.Add(a);
        }
        root.Add("m_ArgumentDependencies", args);
        var special = KVObject.Collection();
        special.Add("m_String", new KVObject("ModelDoc Compiler Version"));
        special.Add("m_CompilerIdentifier", new KVObject("CompileModel"));
        special.Add("m_nFingerprint", U32(3));
        special.Add("m_nUserData", I32(0));
        var specials = KVObject.Array();
        specials.Add(special);
        root.Add("m_SpecialDependencies", specials);
        root.Add("m_SpecialInputDependencies", KVObject.Array());
        root.Add("m_AdditionalRelatedFiles", KVObject.Array());
        root.Add("m_ChildResourceList", KVObject.Array());
        root.Add("m_WeakReferenceList", KVObject.Array());

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var so in model.SceneObjects)
        {
            min = Vector3.Min(min, so.Min);
            max = Vector3.Max(max, so.Max);
        }
        var size = model.SceneObjects.Count == 0 ? Vector3.Zero : max - min;
        var triangles = model.SceneObjects.Sum(so => so.Draws.Sum(d => d.IndexCount / 3));
        // One count per draw set: the first vertex buffer its draws read (a
        // second stream over the same vertices does not add to it).
        var vertices = Enumerable.Range(0, model.IndexBuffers.Count)
            .Select(k => model.SceneObjects.SelectMany(so => so.Draws).FirstOrDefault(d => d.IndexBufferHandle == k))
            .Where(d => d != null && d.VertexBufferHandles.Count > 0)
            .Sum(d => model.VertexBuffers[d!.VertexBufferHandles[0]].Count);
        var user = KVObject.Collection();
        user.Add("bounds_longest", new KVObject((double)MathF.Max(size.X, MathF.Max(size.Y, size.Z))));
        user.Add("bounds_x", new KVObject((double)size.X));
        user.Add("bounds_y", new KVObject((double)size.Y));
        user.Add("bounds_z", new KVObject((double)size.Z));
        user.Add("compile_warnings", I32(model.CompileWarnings));
        user.Add("generate_meshlets", I32(model.Aggregate ? 1 : 0));
        user.Add("IsChildResource", I32(1));
        user.Add("model_animgraph2ref_count", I32(0));
        user.Add("model_archetype_id", new KVObject(""));
        user.Add("model_bodygroup_count", I32(0));
        user.Add("model_bone_count", I32(0));
        user.Add("model_bone_weight_count", I32(4));
        user.Add("model_has_embedded_animation", I32(0));
        user.Add("model_is_modeldoc", I32(1));
        user.Add("model_lod0_triangle_count", I32(triangles));
        user.Add("model_lod0_vertex_count", I32(vertices));
        user.Add("model_lod_count", I32(0));
        user.Add("model_materialgroup_count", I32(0));
        user.Add("model_nmskeletonref_count", I32(0));
        user.Add("model_primary_associated_entity", new KVObject(""));
        user.Add("model_total_triangle_count", I32(triangles));
        user.Add("model_total_vertex_count", I32(vertices));
        user.Add("morph", I32(0));
        user.Add("morph_atlas_pixels", I32(0));
        user.Add("physics_joint_count", I32(0));
        root.Add("m_SearchableUserData", user);
        root.Add("m_SubassetReferences", KVObject.Null());
        root.Add("m_SubassetDefinitions", KVObject.Null());
        return root;
    }

    /// <summary>The model data (DATA): a named model with one mesh group and nothing else.</summary>
    public static KVObject Data(WorldNodeModel model)
    {
        var root = KVObject.Collection();
        root.Add("m_name", new KVObject(model.Name));
        var info = KVObject.Collection();
        info.Add("m_nFlags", U32(8388608));
        info.Add("m_vHullMin", Doubles(0, 0, 0));
        info.Add("m_vHullMax", Doubles(0, 0, 0));
        info.Add("m_vViewMin", Doubles(0, 0, 0));
        info.Add("m_vViewMax", Doubles(0, 0, 0));
        info.Add("m_flMass", new KVObject(0.0));
        info.Add("m_vEyePosition", Doubles(0, 0, 0));
        info.Add("m_flMaxEyeDeflection", new KVObject(0.0));
        info.Add("m_sSurfaceProperty", new KVObject(""));
        info.Add("m_keyValueText", new KVObject(""));
        root.Add("m_modelInfo", info);
        foreach (var key in new[] { "m_ExtParts", "m_refMeshes" })
            root.Add(key, KVObject.Array());
        var meshMasks = KVObject.Array();
        meshMasks.Add(new KVObject(ulong.MaxValue));
        root.Add("m_refMeshGroupMasks", meshMasks);
        root.Add("m_refPhysGroupMasks", KVObject.Array());
        var lodMasks = KVObject.Array();
        lodMasks.Add(new KVObject(255u));
        root.Add("m_refLODGroupMasks", lodMasks);
        foreach (var key in new[] { "m_lodGroupSwitchDistances", "m_refPhysicsData", "m_refPhysicsHitboxData", "m_refAnimGroups",
                                    "m_refSequenceGroups", "m_meshGroups", "m_materialGroups" })
            root.Add(key, KVObject.Array());
        root.Add("m_nDefaultMeshGroupMask", new KVObject(ulong.MaxValue));
        var skeleton = KVObject.Collection();
        foreach (var key in new[] { "m_boneName", "m_nParent", "m_boneSphere", "m_nFlag", "m_bonePosParent", "m_boneRotParent", "m_boneScaleParent" })
            skeleton.Add(key, KVObject.Array());
        root.Add("m_modelSkeleton", skeleton);
        root.Add("m_remappingTable", KVObject.Array());
        var starts = KVObject.Array();
        starts.Add(new KVObject(0u));
        root.Add("m_remappingTableStarts", starts);
        root.Add("m_boneFlexDrivers", KVObject.Array());
        root.Add("m_pModelConfigList", KVObject.Null());
        foreach (var key in new[] { "m_BodyGroupsHiddenInTools", "m_refAnimIncludeModels", "m_AnimatedMaterialAttributes",
                                    "m_animGraph2Refs", "m_vecNmSkeletonRefs" })
            root.Add(key, KVObject.Array());
        return root;
    }

    private static KVObject I32(long v) => v is 0 or 1 ? new KVObject(v) : new KVObject((int)v);

    private static KVObject U32(long v) => v is 0 or 1 ? new KVObject(v) : new KVObject((uint)v);

    private static KVObject Floats(Vector3 v) => Floats(v.X, v.Y, v.Z);

    private static KVObject Floats(params float[] values)
    {
        var a = KVObject.Array();
        foreach (var f in values)
            a.Add(new KVObject(f));
        return a;
    }

    private static KVObject Doubles(params double[] values)
    {
        var a = KVObject.Array();
        foreach (var f in values)
            a.Add(new KVObject(f));
        return a;
    }
}
