# Valve binaries, mapped from their own assert strings

Recovered by `tools/re/dump_asserts.py`. Every assert names the function it
sits in, its source file and its line, so a stripped binary still reports its
own structure. No addresses here; Ghidra's `NameByAsserts.java` adds those.

## resourcecompiler.dll

56,325,784 bytes, 22 functions named by asserts, 117 source files referenced.

| source file | function | line |
|---|---|---|
| `src/animgraphdoclib/animgraphdoc_motionmatchingnode.cpp` | `CAnimGraphDoc_MotionMatchingNode::Compile` | 251 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_bc7e` | 2124 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_ispctexcomp` | 2236 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferETC` | 2360 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::Sanitize` | 5665 |
| `src/bitmap/tinyexr_impl.cpp` | `FloatBitMap_t::WriteInMemoryEXRFast` | 170 |
| `src/mapdoclib/bakedshadowassignment.cpp` | `CComputeBakedShadowAssignment::IncrementalUpdate` | 280 |
| `mathlib/extended/soacontainer.cpp` | `CSOAContainer::FilterInX` | 2526 |
| `src/mathlib/kmeansclustering.cpp` | `BCKMeansClustering::AssignVectorsToClusters` | 159 |
| `src/modeldoc_lib/modeldoc_clothshape_sdf.cpp` | `AxisAlignedDualDepthMap_t::AxisAlignedDualDepthMap_t` | 577 |
| `src/modeldoc_lib/modeldoc_clothshape_sdf.cpp` | `CModelDocClothShapeSDF::FinishCompileRigid` | 1168 |
| `src/modeldoc_lib/modeldoc_modelmodifier.cpp` | `CModelDocModelModifier_FoliageVertexNormals::OperatorSmoothByDistance` | 1148 |
| `src/particleslib/particlemgr.cpp` | `CParticleMgr::SimulateParticles` | 1166 |
| `src/texturelib/amalgamatedtexture.cpp` | `CAmalgamatedTexture::LoadImages` | 311 |
| `utils/resourcecompiler/compiletexture_mip_processors.cpp` | `CTextureFrame::GenerateMips_SphericalHarmonics` | 1011 |
| `utils/resourcecompiler/compiletexture_mip_processors.cpp` | `CTextureFrame::GenerateMips_HemiOctAnisoRoughness` | 1635 |
| `utils/resourcecompiler/compiletexture_mip_processors.cpp` | `CTextureFrame::GenerateMips_HemiOctAnisoRoughness2` | 2078 |
| `resourcecompiler/mapbuilder/uvchartpacker.cpp` | `NUVChartPacker::InitializeChartUVs` | 338 |
| `resourcecompiler/mapbuilder/uvchartpacker.cpp` | `NUVChartPacker::BuildChartTree` | 1248 |
| `resourcecompiler/mapbuilder/visdrivenclustering.cpp` | `CVisibilityMeshMerger::MergeMeshes` | 613 |
| `resourcecompiler/mapbuilder/worldrendererbuildernode.cpp` | `CWorldRendererBuilderNode::FixTJunctionEdgeCracks` | 2071 |
| `resourcecompiler/mapbuilder/worldrendererbuildernode.cpp` | `CWorldRendererBuilderNode::BuildAggregateRTProxies` | 4335 |

<details><summary>103 source files referenced with no assert naming a function</summary>

- `src/animdoclib/clip/animclipdoc_events.cpp`
- `build/src/animgraphdoclib/animgraphdoc_logmanager.cpp`
- `build/src/animlib/animpose.cpp`
- `build/src/animlib/animskeleton.cpp`
- `src/animlib/graph/animgraph_instance.cpp`
- `animlib/graph/nodes/animgraphnode_chainlookat.cpp`
- `animlib/tasksystem/tasks/animtask_chainlookat.cpp`
- `build/src/common/crypto.cpp`
- `build/src/common/opensslwrapper.cpp`
- `build/src/datamodel/dependencygraph.cpp`
- `game/client/renderingpipeline/depth_pyramid_compute_renderer.cpp`
- `build/src/mapdoclib/areafootprinthelper.cpp`
- `build/src/mapdoclib/detailpropobjectmanager.cpp`
- `build/src/mapdoclib/grasstilemgr.cpp`
- `build/src/mapdoclib/hammerclipboard.cpp`
- `build/src/mapdoclib/iconhelper.cpp`
- `build/src/mapdoclib/imageplanenode.cpp`
- `build/src/mapdoclib/mapcompileutils.cpp`
- `build/src/mapdoclib/mapdeformer.cpp`
- `build/src/mapdoclib/mapdoc.cpp`
- `build/src/mapdoclib/mapdotatilegrid.cpp`
- `build/src/mapdoclib/mapfilemanager.cpp`
- `build/src/mapdoclib/mapglobaltilegridmaps.cpp`
- `build/src/mapdoclib/mapinstance.cpp`
- `build/src/mapdoclib/mapmeshlocator.cpp`
- `build/src/mapdoclib/mapnavdata.cpp`
- `build/src/mapdoclib/mapnodefactory.cpp`
- `build/src/mapdoclib/mappath.cpp`
- `build/src/mapdoclib/mappathlocator.cpp`
- `build/src/mapdoclib/mapprefab.cpp`
- `build/src/mapdoclib/mapproxy.cpp`
- `build/src/mapdoclib/mapstaticoverlay.cpp`
- `build/src/mapdoclib/maptilegrid.cpp`
- `build/src/mapdoclib/maptilemesh.cpp`
- `build/src/mapdoclib/maptilemeshgroup.cpp`
- `build/src/mapdoclib/tileinstanceset.cpp`
- `build/src/mapdoclib/tilesetmapmgr.cpp`
- `build/src/mapdoclib/windcontrollerhelper.cpp`
- `build/src/meshutils/triangulatepolygon.cpp`
- `build/src/modeldoc_lib/modeldoc_groommeshfile.cpp`
- `build/src/modeldoc_lib/modeldoc_texture_generator.cpp`
- `build/src/modeldoc_lib/physicsconversion.cpp`
- `build/src/movieobjects/dmelog.cpp`
- `build/src/movieobjects/dmerigconstraintoperators.cpp`
- `build/src/navlib/nav_mesh.cpp`
- `build/src/panorama_content/contentcontext.cpp`
- `build/src/panorama_content/stylecontent.cpp`
- `build/src/particleslib/particle_property.cpp`
- `src/public/appframework/tier2app.h`
- `src/public/fbxsystem/fbxsystem.cpp`
- `src/public/mapdoclib/mapnodefactory.h`
- `src/public/mathlib/range.h`
- `src/public/tier0/check_cast.h`
- `src/public/tier0/threadtoolstypes.h`
- `src/public/tier0/tslist.h`
- `src/public/tier0/utlblockvector.h`
- `src/public/tier0/utldict.h`
- `src/public/tier0/utlmap.h`
- `src/public/tier0/utlrbtree.h`
- `src/public/tier0/utlsortvector.h`
- `build/src/pulsedoc_lib/pulse_doc.cpp`
- `build/src/sounddoc_lib/vmix_node_steamaudiodirect.cpp`
- `build/src/sounddoc_lib/vmix_node_steamaudiohybridreverb.cpp`
- `build/src/sounddoc_lib/vmix_node_stereodelay.cpp`
- `build/src/texturelib/extract.cpp`
- `src/thirdparty/tinybvh/inc_tiny_bvh.h`
- `src/thirdparty/tinybvh/tiny_bvh.h`
- `build/src/toolrenderutils/toolmatsysutils.cpp`
- `build/src/toolrenderutils/toolstandardmodels.cpp`
- `src/tools/toolutils2/detailprops.cpp`
- `utils/resourcecompiler/animation/resourcecompiler_ag2_clip.cpp`
- `utils/resourcecompiler/animation/resourcecompiler_ag2_graph.cpp`
- `utils/resourcecompiler/animation/resourcecompiler_ag2_skeleton.cpp`
- `src/utils/resourcecompiler/compileanimgraph.cpp`
- `src/utils/resourcecompiler/compiledotaherolist.cpp`
- `src/utils/resourcecompiler/compileentitylump.cpp`
- `src/utils/resourcecompiler/compileitemdefs.cpp`
- `src/utils/resourcecompiler/compilekv3.cpp`
- `src/utils/resourcecompiler/compilemanifest.cpp`
- `src/utils/resourcecompiler/compilemap.cpp`
- `src/utils/resourcecompiler/compilematerial.cpp`
- `src/utils/resourcecompiler/compilepanorama.cpp`
- `src/utils/resourcecompiler/compileparticle.cpp`
- `src/utils/resourcecompiler/compilepostprocessing.cpp`
- `src/utils/resourcecompiler/compilesound.cpp`
- `src/utils/resourcecompiler/compilesoundcontainer.cpp`
- `src/utils/resourcecompiler/compilesoundstackscript.cpp`
- `src/utils/resourcecompiler/compiletexture.cpp`
- `src/utils/resourcecompiler/compilevpk.cpp`
- `src/utils/resourcecompiler/compileworld.cpp`
- `src/utils/resourcecompiler/compileworldnode.cpp`
- `utils/resourcecompiler/mapbuilder/lightbaker.cpp`
- `utils/resourcecompiler/mapbuilder/mapbuildercontext.cpp`
- `utils/resourcecompiler/mapbuilder/mapbuilderentity.cpp`
- `utils/resourcecompiler/mapbuilder/staticlightingprocessor.cpp`
- `utils/resourcecompiler/mapbuilder/worldrendererbuilder.cpp`
- `utils/resourcecompiler/modelprocessing/compilemesh.cpp`
- `src/utils/resourcecompiler/relaxedconeshelper.cpp`
- `src/utils/resourcecompiler/resourcecompilercontext.cpp`
- `src/utils/resourcecompiler/resourcecompilersystem.cpp`
- `src/public/vpklib/packedstore.h`
- `build/src/vfx/vfx_common.cpp`
- `build/src/vpklib/packedstore.cpp`

</details>

## visbuilder.dll

1,841,304 bytes, 6 functions named by asserts, 7 source files referenced.

| source file | function | line |
|---|---|---|
| `utils/visbuilder/utils.cpp` | `CBoxMerge::MergeBestCandidates` | 347 |
| `utils/visbuilder/vis3.cpp` | `CVoxelSampler3::MergeClusterSet` | 3227 |
| `utils/visbuilder/vis3.cpp` | `CVoxelSampler3::MergeInsideRegions` | 3304 |
| `utils/visbuilder/vis3.cpp` | `CVoxelSampler3::AdaptivelySampleBorders` | 4718 |
| `utils/visbuilder/vis_cluster.cpp` | `CVisClusterList::RecomputeClusterCostLists` | 488 |
| `utils/visbuilder/vis_cluster.cpp` | `CVisClusterList::RecomputeClusterCostListsForPartition` | 526 |

<details><summary>4 source files referenced with no assert naming a function</summary>

- `src/public/tier0/tslist.h`
- `src/public/tier0/utlblockvector.h`
- `src/utils/visbuilder/visbuilder.cpp`
- `src/utils/visbuilder/voxel_utils.cpp`

</details>

## vrad3.dll

3,146,904 bytes, 19 functions named by asserts, 14 source files referenced.

| source file | function | line |
|---|---|---|
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_bc7e` | 2124 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_ispctexcomp` | 2236 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferETC` | 2360 |
| `src/bitmap/tinyexr_impl.cpp` | `FloatBitMap_t::WriteInMemoryEXRFast` | 170 |
| `src/mathlib/kmeansclustering.cpp` | `CKMeansClustering::AssignVectorsToClusters` | 159 |
| `utils/vrad3/vrad3_gpu.cpp` | `CVrad3Command_path_trace_triangles_gpu::Run` | 3417 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3LightmapImage::InitMip` | 214 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_dilate_over_invalid_jfa::Run` | 725 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_fill_gutters::Run` | 778 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_filter_median::Run` | 960 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_remove_fireflies::Run` | 1109 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_encode_ahd::Run` | 1960 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_lightmap_image_encode_sh2::Run` | 2143 |
| `utils/vrad3/vrad3_lightmap.cpp` | `CVrad3Command_uber_weld::Run` | 2653 |
| `utils/vrad3/vrad3_lightmap_ameliorate_bc_artefacts.cpp` | `CVrad3Command_lightmap_ameliorate_block_compression_artefacts::Run` | 3249 |
| `utils/vrad3/vrad3_lightprobevolume.cpp` | `CVrad3Command_lpv_image_ambient_occlusion::Run` | 908 |
| `utils/vrad3/vrad3_lightprobevolume.cpp` | `CVrad3Command_lpv_image_remove_fireflies::Run` | 970 |
| `utils/vrad3/vrad3_lightprobevolume.cpp` | `CVrad3Command_lpv_image_median_filter::Run` | 1144 |
| `utils/vrad3/vrad3_lightprobevolume.cpp` | `CVrad3Command_lpv_image_median_filter_fog::Run` | 1304 |

<details><summary>7 source files referenced with no assert naming a function</summary>

- `build/src/datamodel/dependencygraph.cpp`
- `src/public/appframework/tier2app.h`
- `src/public/tier0/tslist.h`
- `src/public/tier0/utlblockvector.h`
- `src/public/tier0/utldict.h`
- `src/public/tier0/utlrbtree.h`
- `src/utils/vrad3/vrad3.h`

</details>

## physicsbuilder.dll

13,547,160 bytes, 5 functions named by asserts, 15 source files referenced.

| source file | function | line |
|---|---|---|
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_bc7e` | 2124 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferBC7_ispctexcomp` | 2236 |
| `src/bitmap/floatbitmap.cpp` | `FloatBitMap_t::WriteToBufferETC` | 2360 |
| `src/mathlib/kmeansclustering.cpp` | `CKMeansClustering::AssignVectorsToClusters` | 159 |
| `src/modeldoc_lib/modeldoc_modelmodifier.cpp` | `CModelDocModelModifier_FoliageVertexNormals::OperatorSmoothByDistance` | 1148 |

<details><summary>12 source files referenced with no assert naming a function</summary>

- `build/src/datamodel/dependencygraph.cpp`
- `build/src/modeldoc_lib/modeldoc_texture_generator.cpp`
- `build/src/modeldoc_lib/physicsconversion.cpp`
- `build/src/movieobjects/dmelog.cpp`
- `build/src/movieobjects/dmerigconstraintoperators.cpp`
- `src/public/tier0/tslist.h`
- `src/public/tier0/utlblockvector.h`
- `src/public/tier0/utldict.h`
- `src/public/tier0/utlrbtree.h`
- `build/src/toolrenderutils/toolmatsysutils.cpp`
- `build/src/toolrenderutils/toolstandardmodels.cpp`
- `src/utils/physicsbuilder/physicsbuilder.cpp`

</details>
