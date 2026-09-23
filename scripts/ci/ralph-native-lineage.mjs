const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fail = (message) => { throw new Error(`Native Ralph blocked: ${message}`); };

function relationship(session) {
  if (!uuid.test(session?.id ?? '') ||
      (session.creatorSessionId !== undefined && !uuid.test(session.creatorSessionId))) {
    fail('Malformed native ancestry relationship.');
  }
  return { id: session.id, ...(session.creatorSessionId === undefined ? {} : { creatorSessionId: session.creatorSessionId }) };
}

export function retainNativeLineage(retained = {}, observations, now) {
  const result = { ...retained };
  for (const observation of observations) {
    const session = relationship(observation.session);
    const observed = Date.parse(observation.observedAt);
    if (observation.nativeReadbackVerified !== true || !Number.isFinite(observed) ||
        observed > now || typeof observation.source !== 'string' || !observation.source.trim()) {
      fail('Native ancestry requires a verified readback with its original source and observation time.');
    }
    const previous = result[session.id];
    if (previous && previous.creatorSessionId !== session.creatorSessionId) {
      fail(`Conflicting retained native ancestry for ${session.id}.`);
    }
    result[session.id] ??= { ...session, source: observation.source, observedAt: observation.observedAt };
  }
  for (const [id, entry] of Object.entries(result)) {
    relationship(entry);
    if (id !== entry.id || !Number.isFinite(Date.parse(entry.observedAt)) ||
        Date.parse(entry.observedAt) > now || typeof entry.source !== 'string' || !entry.source.trim()) {
      fail('Malformed retained native ancestry evidence.');
    }
    const visited = new Set();
    let ancestor = entry;
    while (ancestor) {
      if (visited.has(ancestor.id)) fail(`Cyclic Ralph-owned native ancestry at ${ancestor.id}.`);
      visited.add(ancestor.id);
      ancestor = result[ancestor.creatorSessionId];
    }
  }
  return result;
}

export function resolveNativeLineage(sessions, retained, mappedIds, now) {
  const records = retainNativeLineage(retained, [], now);
  const result = new Map();
  for (const session of sessions) {
    relationship(session);
    if (result.has(session.id)) fail('Malformed or duplicate native session lineage.');
    const previous = records[session.id];
    if (previous && (session.nativeReadbackVerified === true || session.creatorSessionId !== undefined) &&
        previous.creatorSessionId !== session.creatorSessionId) {
      fail(`Conflicting retained native ancestry for ${session.id}.`);
    }
    result.set(session.id, {
      ...session,
      ...(previous?.creatorSessionId === undefined ? {} : { creatorSessionId: previous.creatorSessionId }),
    });
  }
  // Only relationships can be recovered. A missing mapped worker still needs fresh evidence.
  for (const session of result.values()) {
    const parent = session.creatorSessionId;
    if (parent && !result.has(parent) && !mappedIds.has(parent) && records[parent]) {
      result.set(parent, relationship(records[parent]));
    }
  }
  return result;
}
