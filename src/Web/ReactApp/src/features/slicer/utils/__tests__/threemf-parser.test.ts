import JSZip from 'jszip';
import { describe, expect, it, vi } from 'vitest';
import { disposeParsedThreeMfModel, parseThreeMfArchive } from '@/features/slicer/utils/threemf-parser';

const MAIN_MODEL_XML = `<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02" xmlns:p="http://schemas.microsoft.com/3dmanufacturing/production/2015/06">
  <resources>
    <object id="1" type="model">
      <components>
        <component objectid="part-a" p:path="/3D/parts/part-a.model" transform="1 0 0 0 1 0 0 0 1 10 20 30" />
      </components>
    </object>
  </resources>
  <build>
    <item objectid="1" transform="1 0 0 0 1 0 0 0 1 5 0 0" />
  </build>
</model>`;

const PART_MODEL_XML = `<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
  <resources>
    <object id="part-a" type="model">
      <mesh>
        <vertices>
          <vertex x="0" y="0" z="0" />
          <vertex x="10" y="0" z="0" />
          <vertex x="0" y="5" z="2" />
        </vertices>
        <triangles>
          <triangle v1="0" v2="1" v3="2" />
        </triangles>
      </mesh>
    </object>
  </resources>
</model>`;

const MODEL_SETTINGS_XML = `<?xml version="1.0" encoding="UTF-8"?>
<config>
  <object id="1">
    <metadata key="name" value="Part A" />
    <metadata key="extruder" value="2" />
    <part id="part-a">
      <metadata key="extruder" value="3" />
    </part>
  </object>
  <plate>
    <metadata key="plater_id" value="3" />
    <model_instance>
      <metadata key="object_id" value="1" />
    </model_instance>
  </plate>
</config>`;

const PLATE_JSON = JSON.stringify({
  bbox_objects: [{ name: 'Part A' }],
});

const DIRECT_MESH_XML = `
<mesh>
  <vertices>
    <vertex x="0" y="0" z="0" />
    <vertex x="10" y="0" z="0" />
    <vertex x="0" y="5" z="2" />
  </vertices>
  <triangles>
    <triangle v1="0" v2="1" v3="2" />
  </triangles>
</mesh>`;

async function createArchive(files: Record<string, string>): Promise<ArrayBuffer> {
  const zip = new JSZip();
  for (const [path, content] of Object.entries(files)) {
    zip.file(path, content);
  }

  return zip.generateAsync({ type: 'arraybuffer' });
}

describe('parseThreeMfArchive', () => {
  it('parses component references, extruder assignments, and plate mappings from a 3MF archive', async () => {
    const archive = await createArchive({
      '3D/3dmodel.model': MAIN_MODEL_XML,
      '3D/parts/part-a.model': PART_MODEL_XML,
      'Metadata/model_settings.config': MODEL_SETTINGS_XML,
      'Metadata/plate_3.json': PLATE_JSON,
    });
    const parsed = await parseThreeMfArchive(archive);

    try {
      expect(parsed.availablePlateIds).toEqual([3]);
      expect(parsed.defaultPlateId).toBe(3);
      expect(parsed.meshes).toHaveLength(1);

      const [mesh] = parsed.meshes;
      expect(mesh.objectId).toBe('1');
      expect(mesh.plateId).toBe(3);
      expect(mesh.extruderIndex).toBe(2);

      mesh.geometry.computeBoundingBox();
      const bounds = mesh.geometry.boundingBox;
      expect(bounds).not.toBeNull();
      expect(bounds?.min.x).toBeCloseTo(15);
      expect(bounds?.min.y).toBeCloseTo(20);
      expect(bounds?.min.z).toBeCloseTo(30);
      expect(bounds?.max.x).toBeCloseTo(25);
      expect(bounds?.max.y).toBeCloseTo(25);
      expect(bounds?.max.z).toBeCloseTo(32);
    } finally {
      disposeParsedThreeMfModel(parsed);
    }
  });

  it('throws a meaningful error for corrupt ZIP input', async () => {
    const invalidArchive = new TextEncoder().encode('not a zip archive').buffer;
    await expect(parseThreeMfArchive(invalidArchive)).rejects.toThrow(/Unsupported or corrupt 3MF archive/i);
  });

  it('throws when the archive does not contain a model file', async () => {
    const archive = await createArchive({
      'Metadata/plate_1.json': JSON.stringify({ objects: [] }),
    });

    await expect(parseThreeMfArchive(archive)).rejects.toThrow(/No model file found/i);
  });

  it('rejects path traversal component paths without loading them', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const archive = await createArchive({
      '3D/3dmodel.model': `<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02" xmlns:p="http://schemas.microsoft.com/3dmanufacturing/production/2015/06">
  <resources>
    <object id="1" type="model">
      ${DIRECT_MESH_XML}
      <components>
        <component objectid="part-a" p:path="../outside.model" />
      </components>
    </object>
  </resources>
  <build>
    <item objectid="1" />
  </build>
</model>`,
    });

    const parsed = await parseThreeMfArchive(archive);

    try {
      expect(parsed.meshes).toHaveLength(1);
      expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('Skipping invalid 3MF component path'));
    } finally {
      warnSpy.mockRestore();
      disposeParsedThreeMfModel(parsed);
    }
  });

  it('resolves same-document component object references', async () => {
    const archive = await createArchive({
      '3D/3dmodel.model': `<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
  <resources>
    <object id="1" type="model">
      <components>
        <component objectid="2" />
      </components>
    </object>
    <object id="2" type="model">
      ${DIRECT_MESH_XML}
    </object>
  </resources>
  <build>
    <item objectid="1" />
  </build>
</model>`,
    });

    const parsed = await parseThreeMfArchive(archive);

    try {
      expect(parsed.meshes).toHaveLength(1);
      expect(parsed.meshes[0].objectId).toBe('1');
    } finally {
      disposeParsedThreeMfModel(parsed);
    }
  });

  it('throws a user-friendly error when a mesh exceeds the triangle limit', async () => {
    const archive = await createArchive({
      '3D/3dmodel.model': '<model />',
    });

    const vertexElements = [{ getAttribute: () => '0' }] as unknown as HTMLCollectionOf<Element>;
    const oversizedTriangleElements = { length: 5_000_001 } as unknown as HTMLCollectionOf<Element>;
    const fakeMeshElement = {
      tagName: 'mesh',
      getElementsByTagName: (tagName: string) => {
        if (tagName === 'vertex') {
          return vertexElements;
        }

        if (tagName === 'triangle') {
          return oversizedTriangleElements;
        }

        return [] as unknown as HTMLCollectionOf<Element>;
      },
    } as unknown as Element;
    const fakeObjectElement = {
      children: [fakeMeshElement],
      getElementsByTagName: () => [] as unknown as HTMLCollectionOf<Element>,
      getAttribute: (attributeName: string) => (attributeName === 'id' ? '1' : null),
      getAttributeNS: () => null,
    } as unknown as Element;
    const fakeDocument = {
      querySelector: () => null,
      getElementsByTagName: (tagName: string) => {
        if (tagName === 'object') {
          return [fakeObjectElement] as unknown as HTMLCollectionOf<Element>;
        }

        return [] as unknown as HTMLCollectionOf<Element>;
      },
    } as unknown as Document;

    const realDomParser = DOMParser;
    class FakeDOMParser {
      parseFromString(): Document {
        return fakeDocument;
      }
    }

    vi.stubGlobal('DOMParser', FakeDOMParser as unknown as typeof DOMParser);

    try {
      await expect(parseThreeMfArchive(archive)).rejects.toThrow(/5,000,000 triangles/i);
    } finally {
      vi.stubGlobal('DOMParser', realDomParser);
    }
  });

  it('skips circular same-document component references instead of recursing forever', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const archive = await createArchive({
      '3D/3dmodel.model': `<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
  <resources>
    <object id="1" type="model">
      ${DIRECT_MESH_XML}
      <components>
        <component objectid="2" />
      </components>
    </object>
    <object id="2" type="model">
      <components>
        <component objectid="1" />
      </components>
    </object>
  </resources>
  <build>
    <item objectid="1" />
  </build>
</model>`,
    });

    const parsed = await parseThreeMfArchive(archive);

    try {
      expect(parsed.meshes).toHaveLength(1);
      expect(warnSpy).toHaveBeenCalledWith(expect.stringContaining('Skipping circular 3MF component reference'));
    } finally {
      warnSpy.mockRestore();
      disposeParsedThreeMfModel(parsed);
    }
  });
});
