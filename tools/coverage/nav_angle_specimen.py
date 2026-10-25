"""nav_angle_specimen.py <probe01.vmap> <out.vmap>: probe01 with func_nav_markup boxes (tag SPLIT, off the
floor so the nav does not change) at node angles outside [0, 360): the markup records navsystem receives
hold each mesh node's matrix (AddMarkup, 180f20720), so a capture (capture_navgen.py, navinput_markups.bin)
shows the angles resourcecompiler built them from. Built as nav_markup_specimen.py builds its map."""
import sys

import nav_markup_specimen as spec

ANGLES = ["0 -30 0", "0 -390 0", "0 390 0", "0 720.25 0", "0 -0.5 0", "0 359.99997 0", "0 -180 0", "0 180.5 0",
          "-45 0 0", "0 0 -100", "-10 -20 -30", "370 -730 1085", "0 -360 0", "0 360 0", "-0.0001 0 0", "0 -1e-06 0"]


def main(base, out):
    markups = [([("navProperty_NavAttributes", "SPLIT")], (-2000 - 300 * k, 4000, 2000), a, (32, 32, 32)) for k, a in enumerate(ANGLES)]
    spec.main(base, out, markups)


if __name__ == "__main__":
    main(*sys.argv[1:3])
