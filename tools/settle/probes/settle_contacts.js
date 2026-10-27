// The world's active contacts around a step window, for capture_settle.py --agent after
// settle_bundle.js. At CRnWorld_Collide's entry ("contacts_in") and exit ("contacts_out") for
// steps CONTACT_FROM..CONTACT_TO: every active contact (world +0xa10 count, +0xa18 array) in
// list order with its sort key (+0x40: proxy A << 32 | proxy B), its two bodies (shape +0x10)
// as world list indices, the contact's first 0x100 bytes and its manifold block (+0x80; count at
// block +4), 0x20 + count * 0xe0 bytes from the block's start.
// RVAs are for vphysics2.dll of 2026-09-24 (collide 0x1f6980, decompiled 1801f6980 / 1801ecfb0).

const VP_CONTACTS = {collide: 0x1f6980};
const CONTACT_FROM = 0, CONTACT_TO = 100;

function contactsOf(world) {
  const n = world.add(0xa10).readS32();
  const cap = world.add(0xa14).readU32() & 0x7fffffff;
  if (n <= 0 || cap === 0) return [];
  const arr = world.add(0xa18).readPointer();
  const bodyIndex = new Map();
  const count = world.add(0x678).readS32(), bodies = world.add(0x680).readPointer();
  for (let i = 0; i < count; i++) bodyIndex.set(bodies.add(i * 8).readPointer().toString(), i);
  const out = [];
  for (let i = 0; i < n; i++) {
    const c = arr.add(i * 8).readPointer();
    const a = c.add(8).readPointer().add(0x10).readPointer(), b = c.add(0x10).readPointer().add(0x10).readPointer();
    const block = c.add(0x80).readPointer();
    const m = block.isNull() ? 0 : block.add(4).readS32();
    out.push({key: c.add(0x40).readU64().toString(), a: bodyIndex.get(a.toString()), b: bodyIndex.get(b.toString()),
              raw: hexOf(c, 0x100), block: block.isNull() ? '' : hexOf(block, 0x20 + Math.max(m, 0) * 0xe0)});
  }
  return out;
}

function hookContacts(vp) {
  Interceptor.attach(vp.base.add(VP_CONTACTS.collide), {
    onEnter(a) {
      this.world = a[0];
      this.on = lastStep >= CONTACT_FROM && lastStep <= CONTACT_TO;
      if (this.on) sendJson({ev: 'contacts_in', step: lastStep, contacts: contactsOf(this.world)});
    },
    onLeave() {
      if (this.on) sendJson({ev: 'contacts_out', step: lastStep, contacts: contactsOf(this.world)});
    },
  });
}

watch('vphysics2.dll', hookContacts);
