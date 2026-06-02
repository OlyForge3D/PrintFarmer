import JSZip from 'jszip';
import * as THREE from 'three';

const MAIN_MODEL_PATH = '3D/3dmodel.model';
const MODEL_SETTINGS_PATH = 'Metadata/model_settings.config';
const PLATE_JSON_PREFIX = 'Metadata/plate_';
const PLATE_JSON_SUFFIX = '.json';
const YIELD_EVERY_N_VERTICES = 20_000;
const YIELD_EVERY_N_TRIANGLES = 20_000;
const PRODUCTION_NAMESPACE = 'http://schemas.microsoft.com/3dmanufacturing/production/2015/06';

interface RawMeshData {
  vertices: number[];
  triangles: number[];
  extruderIndex: number;
}

interface RawObjectData {
  id: string;
  meshes: RawMeshData[];
  defaultExtruderIndex: number;
  plateId: number | null;
  name: string | null;
}

interface BuildItemData {
  objectId: string;
  transform: THREE.Matrix4;
  plateId: number | null;
}

interface ModelSettingsData {
  objectExtruders: Map<string, number>;
  partExtruders: Map<string, number>;
  objectNames: Map<string, string>;
  objectPlateIds: Map<string, number>;
  plateIds: Set<number>;
}

interface PlateJsonData {
  plateIds: Set<number>;
  objectPlateIdsByName: Map<string, number>;
}

interface ThreeMfRenderableMeshSource {
  objectId: string;
  plateId: number | null;
  extruderIndex: number;
  geometry: THREE.BufferGeometry;
}

export interface ThreeMfRenderableMesh {
  objectId: string;
  plateId: number | null;
  extruderIndex: number;
  geometry: THREE.BufferGeometry;
}

export interface ParsedThreeMfModel {
  meshes: ThreeMfRenderableMesh[];
  availablePlateIds: number[];
  defaultPlateId: number | null;
}

function nextTick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function normalizeZipPath(path: string): string {
  return path.replace(/^\/+/, '').replace(/\\/g, '/');
}

function getXmlParserError(doc: Document): string | null {
  const parserError = doc.querySelector('parsererror');
  return parserError?.textContent?.trim() || null;
}

function parseInteger(value: string | null | undefined): number | null {
  if (!value) {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function parseExtruderIndex(value: string | null | undefined): number | null {
  const parsed = parseInteger(value);
  if (parsed == null) {
    return null;
  }

  return Math.max(0, parsed - 1);
}

function getMetadataValue(parent: Element, key: string): string | null {
  for (const child of Array.from(parent.children)) {
    if (child.tagName.toLowerCase().endsWith('metadata') && child.getAttribute('key') === key) {
      return child.getAttribute('value');
    }
  }

  return null;
}

function parsePlateIdFromAttributes(element: Element): number | null {
  for (const attribute of Array.from(element.attributes)) {
    const name = attribute.name.toLowerCase();
    if (
      name === 'plate_id' ||
      name === 'plater_id' ||
      name === 'plateid' ||
      name === 'platerid' ||
      name.endsWith(':plate_id') ||
      name.endsWith(':plater_id')
    ) {
      return parseInteger(attribute.value);
    }
  }

  return null;
}

function parseTransform(transformValue: string | null): THREE.Matrix4 {
  const transform = new THREE.Matrix4();
  if (!transformValue) {
    return transform;
  }

  const values = transformValue
    .trim()
    .split(/\s+/)
    .map((value) => Number.parseFloat(value))
    .filter((value) => Number.isFinite(value));

  if (values.length < 12) {
    return transform;
  }

  transform.set(
    values[0], values[1], values[2], values[9],
    values[3], values[4], values[5], values[10],
    values[6], values[7], values[8], values[11],
    0, 0, 0, 1,
  );

  return transform;
}

async function parseMeshElement(meshElement: Element, extruderIndex: number): Promise<RawMeshData | null> {
  const vertices: number[] = [];
  const triangles: number[] = [];
  const vertexElements = meshElement.getElementsByTagName('vertex');

  for (let index = 0; index < vertexElements.length; index += 1) {
    const vertex = vertexElements[index];
    vertices.push(
      Number.parseFloat(vertex.getAttribute('x') || '0'),
      Number.parseFloat(vertex.getAttribute('y') || '0'),
      Number.parseFloat(vertex.getAttribute('z') || '0'),
    );

    if (index > 0 && index % YIELD_EVERY_N_VERTICES === 0) {
      await nextTick();
    }
  }

  const triangleElements = meshElement.getElementsByTagName('triangle');
  for (let index = 0; index < triangleElements.length; index += 1) {
    const triangle = triangleElements[index];
    triangles.push(
      Number.parseInt(triangle.getAttribute('v1') || '0', 10),
      Number.parseInt(triangle.getAttribute('v2') || '0', 10),
      Number.parseInt(triangle.getAttribute('v3') || '0', 10),
    );

    if (index > 0 && index % YIELD_EVERY_N_TRIANGLES === 0) {
      await nextTick();
    }
  }

  if (vertices.length === 0 || triangles.length === 0) {
    return null;
  }

  return {
    vertices,
    triangles,
    extruderIndex,
  };
}

async function parseDocumentMeshes(doc: Document, defaultExtruderIndex: number, objectId?: string | null): Promise<RawMeshData[]> {
  const meshes: RawMeshData[] = [];
  const rootObjects = Array.from(doc.getElementsByTagName('object'));
  const targetObjects = objectId
    ? rootObjects.filter((element) => element.getAttribute('id') === objectId)
    : rootObjects;

  if (targetObjects.length > 0) {
    for (const targetObject of targetObjects) {
      const directMeshChildren = Array.from(targetObject.children).filter((child) => child.tagName.toLowerCase().endsWith('mesh'));
      for (const meshElement of directMeshChildren) {
        const parsedMesh = await parseMeshElement(meshElement, defaultExtruderIndex);
        if (parsedMesh) {
          meshes.push(parsedMesh);
        }
      }
    }

    return meshes;
  }

  const looseMeshElements = Array.from(doc.getElementsByTagName('mesh'));
  for (const meshElement of looseMeshElements) {
    const parsedMesh = await parseMeshElement(meshElement, defaultExtruderIndex);
    if (parsedMesh) {
      meshes.push(parsedMesh);
    }
  }

  return meshes;
}

async function parseModelSettings(zip: JSZip, parser: DOMParser): Promise<ModelSettingsData> {
  const result: ModelSettingsData = {
    objectExtruders: new Map<string, number>(),
    partExtruders: new Map<string, number>(),
    objectNames: new Map<string, string>(),
    objectPlateIds: new Map<string, number>(),
    plateIds: new Set<number>(),
  };

  const settingsFile = zip.file(MODEL_SETTINGS_PATH);
  if (!settingsFile) {
    return result;
  }

  const xml = await settingsFile.async('string');
  const doc = parser.parseFromString(xml, 'application/xml');
  if (getXmlParserError(doc)) {
    return result;
  }

  for (const objectElement of Array.from(doc.getElementsByTagName('object'))) {
    const objectId = objectElement.getAttribute('id');
    if (!objectId) {
      continue;
    }

    const objectExtruder = parseExtruderIndex(getMetadataValue(objectElement, 'extruder'));
    if (objectExtruder != null) {
      result.objectExtruders.set(objectId, objectExtruder);
    }

    const objectName = getMetadataValue(objectElement, 'name');
    if (objectName) {
      result.objectNames.set(objectId, objectName);
    }

    for (const partElement of Array.from(objectElement.getElementsByTagName('part'))) {
      const partId = partElement.getAttribute('id');
      const partExtruder = parseExtruderIndex(getMetadataValue(partElement, 'extruder'));
      if (partId && partExtruder != null) {
        result.partExtruders.set(`${objectId}:${partId}`, partExtruder);
      }
    }
  }

  for (const plateElement of Array.from(doc.getElementsByTagName('plate'))) {
    const plateId =
      parseInteger(getMetadataValue(plateElement, 'plater_id')) ??
      parseInteger(getMetadataValue(plateElement, 'plate_id'));

    if (plateId == null) {
      continue;
    }

    result.plateIds.add(plateId);

    for (const modelInstance of Array.from(plateElement.getElementsByTagName('model_instance'))) {
      const objectId = getMetadataValue(modelInstance, 'object_id');
      if (objectId) {
        result.objectPlateIds.set(objectId, plateId);
      }
    }
  }

  return result;
}

async function parsePlateJsonMetadata(zip: JSZip): Promise<PlateJsonData> {
  const result: PlateJsonData = {
    plateIds: new Set<number>(),
    objectPlateIdsByName: new Map<string, number>(),
  };

  const plateFiles = Object.keys(zip.files).filter(
    (name) => name.startsWith(PLATE_JSON_PREFIX) && name.endsWith(PLATE_JSON_SUFFIX),
  );

  for (const fileName of plateFiles) {
    const plateIdMatch = fileName.match(/^Metadata\/plate_(\d+)\.json$/);
    const plateId = plateIdMatch ? Number.parseInt(plateIdMatch[1], 10) : Number.NaN;
    if (!Number.isFinite(plateId)) {
      continue;
    }

    result.plateIds.add(plateId);

    try {
      const payload = await zip.files[fileName].async('string');
      const parsed = JSON.parse(payload) as {
        bbox_objects?: Array<{ name?: string }>;
        objects?: Array<{ name?: string }>;
      };

      for (const entry of [...(parsed.bbox_objects ?? []), ...(parsed.objects ?? [])]) {
        if (entry?.name) {
          result.objectPlateIdsByName.set(entry.name, plateId);
        }
      }
    } catch {
      // Ignore malformed plate metadata and keep parsing the main model.
    }
  }

  return result;
}

function createBufferGeometry(vertices: number[], triangles: number[]): THREE.BufferGeometry {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute(vertices, 3));
  geometry.setIndex(triangles);
  geometry.computeVertexNormals();
  geometry.computeBoundingBox();
  geometry.computeBoundingSphere();
  return geometry;
}

async function transformVertices(vertices: number[], transform: THREE.Matrix4): Promise<number[]> {
  const transformed: number[] = [];
  const vector = new THREE.Vector3();

  for (let index = 0; index < vertices.length; index += 3) {
    vector.set(vertices[index], vertices[index + 1], vertices[index + 2]);
    vector.applyMatrix4(transform);
    transformed.push(vector.x, vector.y, vector.z);

    if (index > 0 && index % (YIELD_EVERY_N_VERTICES * 3) === 0) {
      await nextTick();
    }
  }

  return transformed;
}

async function parseObjectMeshes(
  objectElement: Element,
  zip: JSZip,
  parser: DOMParser,
  settings: ModelSettingsData,
  modelCache: Map<string, Document | null>,
): Promise<RawObjectData | null> {
  const objectId = objectElement.getAttribute('id');
  if (!objectId) {
    return null;
  }

  const defaultExtruderIndex = settings.objectExtruders.get(objectId) ?? parseExtruderIndex(
    objectElement.getAttribute('p:extruder') || objectElement.getAttributeNS(PRODUCTION_NAMESPACE, 'extruder'),
  ) ?? 0;

  const objectName = settings.objectNames.get(objectId) ?? getMetadataValue(objectElement, 'name');
  const meshes: RawMeshData[] = [];

  for (const child of Array.from(objectElement.children)) {
    if (child.tagName.toLowerCase().endsWith('mesh')) {
      const parsedMesh = await parseMeshElement(child, defaultExtruderIndex);
      if (parsedMesh) {
        meshes.push(parsedMesh);
      }
    }
  }

  for (const componentElement of Array.from(objectElement.getElementsByTagName('component'))) {
    await nextTick();
    const componentPath = normalizeZipPath(
      componentElement.getAttribute('p:path') || componentElement.getAttributeNS(PRODUCTION_NAMESPACE, 'path') || '',
    );
    if (!componentPath) {
      continue;
    }

    const componentObjectId = componentElement.getAttribute('objectid');
    const partKey = componentObjectId ? `${objectId}:${componentObjectId}` : null;
    const componentExtruderIndex = partKey
      ? settings.partExtruders.get(partKey) ?? defaultExtruderIndex
      : defaultExtruderIndex;

    let componentDoc = modelCache.get(componentPath);
    if (componentDoc === undefined) {
      const componentFile = zip.file(componentPath);
      if (!componentFile) {
        modelCache.set(componentPath, null);
        continue;
      }

      const componentXml = await componentFile.async('string');
      const parsedDoc = parser.parseFromString(componentXml, 'application/xml');
      componentDoc = getXmlParserError(parsedDoc) ? null : parsedDoc;
      modelCache.set(componentPath, componentDoc);
    }

    if (!componentDoc) {
      continue;
    }

    const componentMeshes = await parseDocumentMeshes(componentDoc, componentExtruderIndex, componentObjectId);
    const componentTransform = parseTransform(componentElement.getAttribute('transform'));
    const hasComponentTransform = componentElement.hasAttribute('transform');

    for (const componentMesh of componentMeshes) {
      meshes.push({
        ...componentMesh,
        vertices: hasComponentTransform
          ? await transformVertices(componentMesh.vertices, componentTransform)
          : componentMesh.vertices,
      });
    }
  }

  if (meshes.length === 0) {
    return null;
  }

  return {
    id: objectId,
    meshes,
    defaultExtruderIndex,
    plateId: parsePlateIdFromAttributes(objectElement) ?? settings.objectPlateIds.get(objectId) ?? null,
    name: objectName ?? null,
  };
}

function findMainModelPath(zip: JSZip): string | null {
  if (zip.file(MAIN_MODEL_PATH)) {
    return MAIN_MODEL_PATH;
  }

  return Object.keys(zip.files).find((name) => name.endsWith('/3dmodel.model') || name.endsWith('.model')) ?? null;
}

function collectAvailablePlateIds(meshes: ThreeMfRenderableMeshSource[], settings: ModelSettingsData, plateJsonData: PlateJsonData): number[] {
  const plateIds = new Set<number>();

  for (const mesh of meshes) {
    if (mesh.plateId != null) {
      plateIds.add(mesh.plateId);
    }
  }

  for (const plateId of settings.plateIds) {
    plateIds.add(plateId);
  }

  for (const plateId of plateJsonData.plateIds) {
    plateIds.add(plateId);
  }

  return [...plateIds].sort((left, right) => left - right);
}

/**
 * Parse a 3MF archive into ready-to-render Three.js geometries.
 *
 * @remarks
 * The slicer workspace is already Z-up, so 3MF coordinates are preserved.
 */
export async function parseThreeMfArchive(arrayBuffer: ArrayBuffer): Promise<ParsedThreeMfModel> {
  let zip: JSZip;
  try {
    zip = await JSZip.loadAsync(arrayBuffer);
  } catch {
    throw new Error('Unsupported or corrupt 3MF archive.');
  }

  const parser = new DOMParser();
  const settings = await parseModelSettings(zip, parser);
  const plateJsonData = await parsePlateJsonMetadata(zip);
  const mainModelPath = findMainModelPath(zip);

  if (!mainModelPath) {
    throw new Error('The 3MF archive does not contain a model definition.');
  }

  const mainModelFile = zip.file(mainModelPath);
  if (!mainModelFile) {
    throw new Error('The 3MF archive is missing its main model file.');
  }

  const mainXml = await mainModelFile.async('string');
  const mainDoc = parser.parseFromString(mainXml, 'application/xml');
  if (getXmlParserError(mainDoc)) {
    throw new Error('The 3MF model XML could not be parsed.');
  }

  const objectMap = new Map<string, RawObjectData>();
  const modelCache = new Map<string, Document | null>();

  for (const objectElement of Array.from(mainDoc.getElementsByTagName('object'))) {
    const parsedObject = await parseObjectMeshes(objectElement, zip, parser, settings, modelCache);
    if (parsedObject) {
      objectMap.set(parsedObject.id, parsedObject);
    }
  }

  const buildItems: BuildItemData[] = [];
  const buildElement = mainDoc.getElementsByTagName('build')[0];
  if (buildElement) {
    for (const itemElement of Array.from(buildElement.getElementsByTagName('item'))) {
      const objectId = itemElement.getAttribute('objectid');
      if (!objectId) {
        continue;
      }

      const objectData = objectMap.get(objectId);
      const itemNamePlateId = objectData?.name ? plateJsonData.objectPlateIdsByName.get(objectData.name) ?? null : null;
      buildItems.push({
        objectId,
        transform: parseTransform(itemElement.getAttribute('transform')),
        plateId:
          parsePlateIdFromAttributes(itemElement) ??
          objectData?.plateId ??
          itemNamePlateId ??
          null,
      });
    }
  }

  const renderableMeshes: ThreeMfRenderableMeshSource[] = [];

  if (buildItems.length > 0) {
    for (const buildItem of buildItems) {
      const objectData = objectMap.get(buildItem.objectId);
      if (!objectData) {
        continue;
      }

      for (const mesh of objectData.meshes) {
        renderableMeshes.push({
          objectId: buildItem.objectId,
          plateId: buildItem.plateId,
          extruderIndex: mesh.extruderIndex,
          geometry: createBufferGeometry(await transformVertices(mesh.vertices, buildItem.transform), mesh.triangles),
        });
      }
    }
  } else {
    for (const objectData of objectMap.values()) {
      const plateId = objectData.name ? plateJsonData.objectPlateIdsByName.get(objectData.name) ?? objectData.plateId : objectData.plateId;
      for (const mesh of objectData.meshes) {
        renderableMeshes.push({
          objectId: objectData.id,
          plateId,
          extruderIndex: mesh.extruderIndex,
          geometry: createBufferGeometry(mesh.vertices, mesh.triangles),
        });
      }
    }
  }

  if (renderableMeshes.length === 0) {
    throw new Error('The 3MF archive does not contain any mesh geometry.');
  }

  const availablePlateIds = collectAvailablePlateIds(renderableMeshes, settings, plateJsonData);

  return {
    meshes: renderableMeshes,
    availablePlateIds,
    defaultPlateId: availablePlateIds[0] ?? null,
  };
}

export function disposeParsedThreeMfModel(model: ParsedThreeMfModel | null | undefined): void {
  if (!model) {
    return;
  }

  for (const mesh of model.meshes) {
    mesh.geometry.dispose();
  }
}
