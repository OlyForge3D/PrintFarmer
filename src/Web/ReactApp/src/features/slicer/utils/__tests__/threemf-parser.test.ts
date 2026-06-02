import JSZip from 'jszip';
import { describe, expect, it } from 'vitest';
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

describe('parseThreeMfArchive', () => {
  it('parses component references, extruder assignments, and plate mappings from a 3MF archive', async () => {
    const zip = new JSZip();
    zip.file('3D/3dmodel.model', MAIN_MODEL_XML);
    zip.file('3D/parts/part-a.model', PART_MODEL_XML);
    zip.file('Metadata/model_settings.config', MODEL_SETTINGS_XML);
    zip.file('Metadata/plate_3.json', PLATE_JSON);

    const archive = await zip.generateAsync({ type: 'arraybuffer' });
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
});
