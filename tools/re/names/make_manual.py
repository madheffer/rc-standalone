"""make_manual.py -- regenerate the hand-kept name lists (<dll>.manual.json) in this folder.

Every name here was established by reading the function (the port that
matches Valve's output cites it). apply_names.py writes them into Ghidra with
their module tag. Add to these lists as functions are read; rerun this to
rewrite the JSON.
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
H = "s2c:physics/hull"
S = "s2c:physics/svm"
M = "s2c:physics/mesh"
SI = "s2c:physics/simplifier"
B = "s2c:mapbuilder"
G = "s2c:geometry/mesh-export"
W = "s2c:physics/weld"
X = "s2c:math"
I = "s2c:infra"
P = "s2c:pipeline"
RTE = "s2c:geometry/rte"
WN = "s2c:geometry/world-nodes"
PROPS = "s2c:geometry/props"
VIS = "s2c:visibility"
ENT = "s2c:entities"
LIGHTS = "s2c:baked/light-keys"
BAKE = "s2c:baked/lighting"
PM = "s2c:physics/model"
PW = "s2c:physics/world"
NAV = "s2c:baked/nav"

RESOURCECOMPILER = [
    ("1802c4a20", "ShapeBuilder_LoopShapes", H, "shape loop over a physics model"),
    ("180c253c0", "ShapeBuilder_BuildHullShape", H, "RnHullCreate through the interface, the region SVM, then the move"),
    ("1819595d0", "RnHull_Transform", H, "moves a cooked hull by a 3x4: planes, mass, ortho areas, SVM planes"),
    ("1819f2a70", "SchemaBind_RnHull_t", H, ""),
    ("1819d08a0", "RegionSvm_Init", S, "regions and planes"),
    ("1819d2250", "RegionSvm_Build", S, "breadth-first splits"),
    ("1819d25c0", "RegionSvm_Write", S, "flatten to m_Planes and m_Nodes"),
    ("1819d18d0", "RegionSvm_Free", S, ""),
    ("1819d45c0", "RegionSvm_RootRegion", S, ""),
    ("1819d3e90", "RegionSvm_EdgeRegion", S, ""),
    ("1819d41c0", "RegionSvm_FaceRegion", S, ""),
    ("1819d4740", "RegionSvm_VertexRegion", S, ""),
    ("1819d4d70", "RegionSvm_SplitNode", S, ""),
    ("1819d2c80", "RegionSvm_ChoosePlane", S, ""),
    ("1819d2740", "RegionSvm_SeparatingPlaneFallback", S, ""),
    ("1819d3600", "RegionSvm_Separator", S, "edge-edge and face planes between two regions"),
    ("1819d34c0", "RegionSvm_RegionEdge", S, ""),
    ("1819cfc20", "RegionSvm_ClipRegion", S, "dead: the clip flag is never set"),
    ("1819d1ec0", "RegionSvm_NewTreeNode", S, ""),
    ("181083540", "MapBuilder_ResolvePhysicsType", B, "default is convex_multi in an entity, mesh outside"),
    ("1801ff720", "MapBuilder_BuildPhysicsPiece", B, "per piece: hulls for types 2 and 3, else a modeldoc mesh node"),
    ("18020b230", "MapBuilder_BuildPhysicsPieces", B, "copy piece CMesh, weld 1/32, two CTransforms, half-edge build"),
    ("18131f680", "MapBuilder_HullsFromMesh", H, "modes 0 single, 1 per mesh, 2 per element"),
    ("18131efc0", "MapBuilder_HullBuild", H, "same code as vphysics2's RnHull_BuildFromPoints"),
    ("1814e81b0", "MapBuilder_CreateHullNodes", H, ""),
    ("18130cb80", "MapBuilder_SplitElements", H, ""),
    ("18130c0d0", "MapBuilder_GroupTriangles", H, "breadth-first groups sharing a vertex"),
    ("18130f460", "MapBuilder_ElementVertexSet", H, ""),
    ("1813089b0", "MapBuilder_MeshFromMapMesh", B, ""),
    ("181308060", "MapBuilder_TriangleMesh", B, "positions joined, vertices in corner order"),
    ("181310a90", "MapBuilder_TriangulateFace", B, "quad diagonal rule, then an ear clipper"),
    ("1813207c0", "HullSimplifier_Limits", SI, ""),
    ("1813d3db0", "HullSimplifier_Algorithm0", SI, ""),
    ("1813e0460", "HullSimplifier_Algorithm1", SI, "not ported"),
    ("1813f11f0", "HullSimplifier_Algorithm2", SI, "not ported"),
    ("1813d1ed0", "HullSimplifier_NeedsSimplify", SI, ""),
    ("1813fc5d0", "Qem_Simplify", SI, ""),
    ("1813f9e70", "Qem_Adjacency", SI, ""),
    ("1813fae30", "Qem_Quadrics", SI, ""),
    ("1813fd060", "Qem_EdgeCost", SI, ""),
    ("1813fa630", "Qem_Collapse", SI, ""),
    ("1813fc310", "Qem_Retarget", SI, ""),
    ("1813fbf30", "Qem_Flip", SI, ""),
    ("1813fb800", "Qem_Link", SI, ""),
    ("1813d02e0", "HullAgglomerator_Ctor", SI, ""),
    ("1813d06c0", "HullAgglomerator_Cost", SI, ""),
    ("1813d3f00", "HullAgglomerator_Rebuild", SI, ""),
    ("181bfd190", "HullFromPlanes", SI, "dual quickhull"),
    ("1812d80c0", "CMesh_Weld", W, "per-float tolerances, KD tree clusters"),
    ("1812e5880", "CMesh_WeldZeroSubset", W, ""),
    ("1812e2530", "CMesh_WeldCluster", W, ""),
    ("1812dd2f0", "CMesh_RemoveDegenerateAndRenumber", W, ""),
    ("1812d66f0", "CMesh_Copy", W, ""),
    ("1812d6a90", "CMesh_Alloc", W, ""),
    ("18125d940", "AngleQuaternion", X, "half angles times 0.00872664619"),
    ("181260150", "QuaternionMatrix", X, ""),
    ("181253780", "CTransform_Invert", X, "scale 1 path"),
    ("1802b1ff0", "CMesh_TransformByMatrix", X, ""),
    ("18125d1f0", "Matrix3x4_TransformPoint", X, "(t + y r1) + (x r0 + z r2)"),
    ("18125d1b0", "Matrix3x4_Rotate", X, "(x r0 + y r1) + z r2"),
    ("180ffdf20", "MapNode_WorldMatrix", B, "instance path times AngleMatrix"),
    ("180ffddd0", "MapInstance_StepMatrix", B, ""),
    ("18023bd00", "MapBuilder_ReadMeshBuffers", G, "asks the node for its mesh DMX list"),
    ("180f74350", "MapNode_GetMeshBuffersForwarder", G, ""),
    ("1810dff20", "CMapMesh_ConvertMeshForBuilder", G, "the DMX of the mesh the builder reads"),
    ("180ffd010", "MapMeshBuffer_Unserialize", G, "hammerMeshDataBuffer"),
    ("180d47810", "DmeMeshToCMeshes", G, ""),
    ("180d47b90", "DmeMeshToCMesh", G, "stream layout from the DMX"),
    ("180d5c570", "CMesh_WriteDmeMesh", G, ""),
    ("1810c1eb0", "HammerMesh_CopyFrom", G, ""),
    ("1810c4e50", "HammerMesh_TransformToWorld", G, "normals and tangents rotated, not renormalised"),
    ("1810e7020", "HammerMesh_TexcoordsOutOfRange", G, "outside +-1.03125"),
    ("1810d93c0", "HammerMesh_ShiftTexcoordIslands", G, ""),
    ("1813b6550", "PolyMesh_FindTexcoordIslands", G, ""),
    ("1813b6190", "PolyMesh_EdgeTexcoordsContinuous", G, "squared distance at most 1e-6 at both ends"),
    ("1813cc880", "TexcoordIsland_Recentre", G, "box centre rounded half away from zero"),
    ("18140ebc0", "ModelDoc_CreateNode", B, "node factory; the mesh physics branch makes one per piece"),
    ("181373580", "PerfScope_Begin", I, "scope name in rdx; harvest_names.py uses it"),
    # The map compile's spine (CWorldRendererBuilder::Build and what it calls).
    ("1802472a0", "CWorldRendererBuilder::Build", P, "the -world phase: dirs, scene, RTE, world, entity lumps"),
    ("1801f9ad0", "CMapBuilderContext::InitializeIntermediateOutputDir", P, ""),
    ("180f679c0", "Step_LoadingMap", P, "Loading map: %s"),
    ("18024a0d0", "CWorldRendererBuilder::CreateRayTracingEnvironment", RTE, "the .rte visibility traces"),
    ("18024b890", "CWorldRendererBuilder::CompileAndSaveNodes", WN, ""),
    ("180282ef0", "Step_BuildingRenderClusters", WN, "VisibilityGuidedMeshClustering"),
    ("180260710", "Step_SplittingMeshWith", WN, "Splitting mesh with %i verts %i tris"),
    ("180259b00", "Step_RemovingTrianglesInside", WN, "culling boxes"),
    ("180277ab0", "Step_BuildingVertexOverrideStreams", WN, ""),
    ("180259d30", "CWorldRendererBuilderNode::FixTJunctionEdgeCracks", WN, ""),
    ("1802636b0", "CWorldRendererBuilderNode::BuildAggregateRTProxies", WN, ""),
    ("180281100", "CWorldRendererBuilderNode::PostCompileNode", WN, ""),
    ("180287320", "CWorldRendererBuilderNode::LoadMaterialsInMeshList", WN, ""),
    ("18026e490", "WRB_RemoveZeroExtraAttributeStreams", WN, "Removed %i all-zero extra attribute streams"),
    ("18026d9e0", "WRB_LoadStaticPropModels", PROPS, "Failed to load model for prop %s"),
    ("180234180", "CVisibilityMeshMerger::MergeMeshes", VIS, ""),
    ("18024d710", "WRB_CreateEntityTemplateLumps", ENT, "create_entity_template_lumps"),
    ("18024adc0", "WRB_WriteEntityLump", ENT, "entity_lump_params, entities\\"),
    ("180247d40", "WRB_PrecomputeLightVisMembership", LIGHTS, "precomputed_vis_clusters, light_barn"),
    ("180248800", "WRB_BakePrecomputedShadows", BAKE, "direct_light_shadows"),
    ("18023af80", "Vrad3_Init", BAKE, "VRAD3_PATH, Vrad3_Init_SearchPath"),
    ("180285eb0", "WRB_BuildPathTraceSceneInfo", BAKE, "lights, cameras, instances, mesh_file"),
    ("180217740", "CStaticLightingProcessor::BakeLighting", BAKE, ""),
    ("18025db60", "CWorldRendererBuilderNode::BakeLightMaps", BAKE, ""),
    ("18032e3e0", "CModelDocCompileInstance::CompilePhysics", PM, "a model's physics, world_physics.vmdl included"),
    ("1818d77e0", "CNavMesh::CreateArea", NAV, ""),
    ("1818dcb00", "CNavMesh::Update", NAV, ""),
]

VPHYSICS2 = [
    ("1801ac5c0", "RnHullCreate", H, ""),
    ("1801ac890", "RnHullCreateBox", H, ""),
    ("1801303b0", "RnHull_Aabb", H, ""),
    ("1803843f0", "RnHull_BuildFromPoints", H, "normalise, then quickhull"),
    ("1803c3440", "QuickHull_Init", H, ""),
    ("1803c5650", "QuickHull_Build", H, ""),
    ("1803c6300", "QuickHull_IsValid", H, ""),
    ("1803c3510", "QuickHull_Destroy", H, ""),
    ("180384ab0", "RnHull_Extrude", H, ""),
    ("180385370", "RnHull_Limits", H, ""),
    ("180385100", "RnHull_CheckInnerMargin", H, ""),
    ("180384f80", "RnHull_Result", H, ""),
    ("1801a7310", "RnHull_FromQuickHull", H, ""),
    ("180292700", "RnHull_MassProperties", H, ""),
    ("18012ffd0", "RnHull_SurfaceArea", H, ""),
    ("1801302f0", "RnHull_CentroidRadius", H, ""),
    ("1801ad8a0", "RnMeshCreate", M, ""),
    ("180383b00", "CMesh_WeldPositions", M, "clusters kept in formation order"),
    ("1801a4070", "RnMesh_BuildBvhNode", M, "recursive, leaves of four"),
    ("1801aab10", "RnMesh_SahSplit", M, "32 bins per axis"),
    ("1801a93e0", "RnMesh_HalveSplit", M, ""),
    ("1801a3a70", "RnMesh_NewNode", M, ""),
    ("1801a8960", "RnMesh_ReorderLeaves", M, ""),
    ("180165750", "RnMesh_BuildEdgeAdjacency", M, ""),
    ("1801673a0", "RnMesh_ComputeFlags", M, "closed, inverted"),
    ("180164000", "RnMesh_SelfCollide", M, "the BVH against itself"),
    ("180165350", "RnMesh_TrianglePairTouches", M, "skips a shared vertex; touching below 1.19e-7"),
    ("18008d0b0", "TetrahedronVolume", M, ""),
    ("180166ff0", "RnMesh_OrthographicAreas", M, ""),
]

PHYSICSBUILDER = [
    ("18015aff0", "PhysicsBuilder_BuildHullShape", H, "%s_hull_shape"),
    ("18015f450", "PhysicsBuilder_HullOptions", H, "from the CModelDocPhysicsHullFile attributes"),
    ("18015b640", "PhysicsBuilder_CopyHullVertices", H, ""),
    ("1800d4600", "PhysicsBuilder_QuickHull", H, ""),
    ("1800d3f40", "PhysicsBuilder_QuickHullBuild", H, ""),
    ("1800d5310", "QuickHull_VertexList", H, ""),
    ("1800142d0", "CPhysicsBuilder::Build", PW, "writes world_physics.vmdl; Physics/EnableWorldCompounds"),
]


def write(name, rows):
    with open(os.path.join(HERE, name), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"functions": [{"addr": a, "name": n, "module": m, "note": note} for a, n, m, note in rows]}, f, indent=1)


if __name__ == "__main__":
    write("resourcecompiler_20260923.manual.json", RESOURCECOMPILER)
    write("vphysics2_20260924.manual.json", VPHYSICS2)
    write("physicsbuilder_20260924.manual.json", PHYSICSBUILDER)
    print(len(RESOURCECOMPILER), len(VPHYSICS2), len(PHYSICSBUILDER))
