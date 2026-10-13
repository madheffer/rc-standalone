# Meshes: vertex and index buffers as resourcecompiler writes them

A CS2 model keeps its mesh data in its own blocks: `CTRL.embedded_meshes[]` describes each
mesh's `m_vertexBuffers` and `m_indexBuffers` (`m_nBlockIndex` into the file's blocks,
`m_nElementCount`, `m_nElementSizeInBytes`, `m_bMeshoptCompressed`, `m_bCompressedZSTD`,
`m_bMeshoptIndexSequence`, `m_nMeshoptMeshletEncodeVersion`, the input layout), and the bytes
live in `MVTX` and `MIDX`. pak01 (1.41.9.0, 13,105 models): 1,551 vertex buffers in meshopt
format v1 (`A1`, current compiles, which also carry `m_nMeshoptMeshletEncodeVersion -1`),
1,556 in v0 (`A0`, older compiles), 3,322 meshopt index buffers (`E1`), and a few hundred
uncompressed or zstd buffers.

## The codecs: `Meshes.MeshoptVertexCodec`, `Meshes.MeshoptIndexCodec`

Ours, from the format (meshoptimizer's, MIT; VRF's decoders read it the same way), checked
against RC's bytes rather than taken from source:

- **Decode**: vertex v0 and v1, index v0 and v1. Every buffer of the pak01 sample decodes to
  the same bytes as VRF's decoders.
- **Vertex encode (v1)**, byte-identical on all 1,551 v1 vertex buffers in pak01:
  - per 4-byte channel a mode, stored in the tail: 0 byte deltas (zigzag), 1 16-bit deltas
    (zigzag), 2 32-bit xor rotated left by the high nibble;
  - the mode is estimated, not minimised: the rotation (0..7) by OR-ing the bits that change
    between vertices over groups of 16 vertices, rotating, and summing a width estimate per
    byte (0, 2, 4 or 8 bits); then each mode's deltas over every third block (from the vertex
    before the block), each 16-byte group costed at its cheapest of 1, 2, 4 and 8 bits; the
    smallest total wins, the lower mode on a tie (271 of 271 channels of 40 buffers);
  - per block and byte lane a control: 2 when all zero, else the cheaper bit table (0 for
    0/1/2/4 bits, 1 for 1/2/4/8; 1 on a tie), 3 (literal) when that is not smaller than the
    raw bytes;
  - per 16-byte group the cheapest width, starting from the widest, a tie going to the previous
    group's width unless the widest holds it ("favor consistent bit selection across groups,
    but never replace literals").
- **Index encode (v1)**: edge and vertex FIFOs, the fixed code-aux table, `last ± 1` codes,
  the reset on `0 1 2`. Byte-identical on all 3,322 index buffers; meshoptimizer 0.18.1's
  encoder agrees on every one. A decoded triangle may come back rotated (same winding).

`MeshoptCodecTests` round-trips synthetic data and re-encodes RC's buffers from the first
1,500 models of pak01 when CS2 is installed.

## Open

- v0 vertex encoding (older compiles only; meshoptimizer 0.18.1 matches 6 of 40, so Valve's
  encoder for those was another version). Not needed for new output.
- The zstd layer (`m_bCompressedZSTD`) and meshlet encodings (`m_nMeshoptMeshletEncodeVersion`
  other than -1), seen on a few buffers.
- Wiring: typed model views that decode a buffer, let a caller edit it, and write it back
  through these encoders with the CTRL descriptor kept consistent.
