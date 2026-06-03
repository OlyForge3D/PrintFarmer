import JSZip from 'jszip';
import * as THREE from 'three';

const MAIN_MODEL_PATH = '3D/3dmodel.model';
const MODEL_SETTINGS_PATH = 'Metadata/model_settings.config';
const PLATE_JSON_PREFIX = 'Metadata/plate_';
const PLATE_JSON_SUFFIX = '.json';
const YIELD_EVERY_N_VERTICES = 20_000;
const YIELD_EVERY_N_TRIANGLES = 20_000;
const PRODUCTION_NAMESPACE = 'http://schemas.microsoft.com/3dmanufacturing/production/2015/06';
const MAX_ENTRY_SIZE = 200 * 1024 * 1024;
const MAX_TOTAL_SIZE = 500 * 1024 * 1024;
const MAX_XML_SIZE = 50 * 1024 * 1024;
const MAX_TOTAL_TRIANGLES = 5_000_000;
const MAX_TOTAL_VERTICES = 15_000_000;
const MAX_RENDER_INSTANCES = 1_000;

/** Thrown for security/resource-limit violations. Must NOT trigger STL fallback. */
export class ThreeMfSecurityError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ThreeMfSecurityError';
  }
}

/** Tracks cumulative mesh complexity across the entire archive. */
interface ComplexityBudget {
  totalVertices: number;
  totalTriangles: number;
}

interface ZipEntryWithSize extends JSZip.JSZipObject {
  _data?: {
    uncompressedSize?: number;
  };
}

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
  buildItemIndex: number;
  plateId: number | null;
  extruderIndex: number;
  geometry: THREE.BufferGeometry;
}

export interface ThreeMfRenderableMesh {
  objectId: string;
  buildItemIndex: number;
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

function getZipEntryUncompressedSize(entry: JSZip.JSZipObject): number {
  const size = (entry as ZipEntryWithSize)._data?.uncompressedSize;
  return Number.isFinite(size) && size != null && size >= 0 ? size : 0;
}

function validateZipEntrySizes(zip: JSZip): void {
  let totalUncompressedSize = 0;

  for (const entry of Object.values(zip.files)) {
    if (entry.dir) {
      continue;
    }

    const entrySize = getZipEntryUncompressedSize(entry);
    if (entrySize > MAX_ENTRY_SIZE) {
      throw new ThreeMfSecurityError('The 3MF archive contains a file that exceeds the 200 MB unpacked size limit.');
    }

    totalUncompressedSize += entrySize;
    if (totalUncompressedSize > MAX_TOTAL_SIZE) {
      throw new ThreeMfSecurityError('The 3MF archive exceeds the 500 MB unpacked size limit.');
    }
  }
}

function normalizeZipPath(path: string): string {
  const normalizedPath = path.replace(/^\/+/, '').replace(/\\/g, '/');
  if (!normalizedPath) {
    return '';
  }

  const segments = normalizedPath.split('/');
  return segments.some((segment) => segment === '..') ? '' : normalizedPath;
}

function assertXmlSize(xml: string): void {
  if (xml.length > MAX_XML_SIZE) {
    throw new ThreeMfSecurityError('The 3MF archive contains an XML file that exceeds the 50 MB size limit.');
  }
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

async function parseMeshElement(meshElement: Element, extruderIndex: number, budget: ComplexityBudget): Promise<RawMeshData | null> {
  const vertices: number[] = [];
  const triangles: number[] = [];
  const vertexElements = meshElement.getElementsByTagName('vertex');

  if (vertexElements.length > MAX_TOTAL_VERTICES) {
    throw new ThreeMfSecurityError(`The 3MF mesh is too complex to render safely (more than ${MAX_TOTAL_VERTICES.toLocaleString()} vertices).`);
  }

  budget.totalVertices += vertexElements.length;
  if (budget.totalVertices > MAX_TOTAL_VERTICES) {
    throw new ThreeMfSecurityError(`The 3MF archive exceeds the cumulative vertex limit of ${MAX_TOTAL_VERTICES.toLocaleString()}.`);
  }

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
  if (triangleElements.length > MAX_TOTAL_TRIANGLES) {
    throw new ThreeMfSecurityError(`The 3MF mesh is too complex to render safely (more than ${MAX_TOTAL_TRIANGLES.toLocaleString()} triangles).`);
  }

  budget.totalTriangles += triangleElements.length;
  if (budget.totalTriangles > MAX_TOTAL_TRIANGLES) {
    throw new ThreeMfSecurityError(`The 3MF archive exceeds the cumulative triangle limit of ${MAX_TOTAL_TRIANGLES.toLocaleString()}.`);
  }

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

function findObjectElementById(doc: Document, objectId: string): Element | null {
  return Array.from(doc.getElementsByTagName('object')).find((element) => element.getAttribute('id') === objectId) ?? null;
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
  assertXmlSize(xml);
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

async function loadModelDocument(
  modelPath: string,
  zip: JSZip,
  parser: DOMParser,
  modelCache: Map<string, Document | null>,
): Promise<Document | null> {
  let modelDocument = modelCache.get(modelPath);
  if (modelDocument !== undefined) {
    return modelDocument;
  }

  const componentFile = zip.file(modelPath);
  if (!componentFile) {
    modelCache.set(modelPath, null);
    return null;
  }

  const componentXml = await componentFile.async('string');
  assertXmlSize(componentXml);
  const parsedDoc = parser.parseFromString(componentXml, 'application/xml');
  modelDocument = getXmlParserError(parsedDoc) ? null : parsedDoc;
  modelCache.set(modelPath, modelDocument);
  return modelDocument;
}

async function parseComponentMeshes(
  doc: Document,
  documentPath: string,
  targetObjectId: string | null,
  zip: JSZip,
  parser: DOMParser,
  settings: ModelSettingsData,
  modelCache: Map<string, Document | null>,
  objectCache: Map<string, RawObjectData | null>,
  visited: Set<string>,
  overrideExtruderIndex: number,
  budget: ComplexityBudget,
): Promise<RawMeshData[]> {
  const targetObjects = targetObjectId
    ? [findObjectElementById(doc, targetObjectId)].filter((element): element is Element => element != null)
    : Array.from(doc.getElementsByTagName('object'));

  const meshes: RawMeshData[] = [];
  for (const targetObject of targetObjects) {
    const parsedObject = await parseObjectMeshes(
      targetObject,
      zip,
      parser,
      settings,
      modelCache,
      objectCache,
      doc,
      documentPath,
      visited,
      budget,
    );

    if (!parsedObject) {
      continue;
    }

    for (const mesh of parsedObject.meshes) {
      meshes.push({
        ...mesh,
        extruderIndex: overrideExtruderIndex,
      });
    }
  }

  return meshes;
}

async function parseObjectMeshes(
  objectElement: Element,
  zip: JSZip,
  parser: DOMParser,
  settings: ModelSettingsData,
  modelCache: Map<string, Document | null>,
  objectCache: Map<string, RawObjectData | null>,
  currentDoc: Document,
  currentDocumentPath: string,
  visited: Set<string> = new Set(),
  budget: ComplexityBudget = { totalVertices: 0, totalTriangles: 0 },
): Promise<RawObjectData | null> {
  const objectId = objectElement.getAttribute('id');
  if (!objectId) {
    return null;
  }

  const cacheKey = `${currentDocumentPath}#${objectId}`;
  const cachedObject = objectCache.get(cacheKey);
  if (cachedObject !== undefined) {
    return cachedObject;
  }

  const activeVisited = new Set(visited);
  activeVisited.add(cacheKey);

  const defaultExtruderIndex = settings.objectExtruders.get(objectId) ?? parseExtruderIndex(
    objectElement.getAttribute('p:extruder') || objectElement.getAttributeNS(PRODUCTION_NAMESPACE, 'extruder'),
  ) ?? 0;

  const objectName = settings.objectNames.get(objectId) ?? getMetadataValue(objectElement, 'name');
  const meshes: RawMeshData[] = [];

  for (const child of Array.from(objectElement.children)) {
    if (child.tagName.toLowerCase().endsWith('mesh')) {
      const parsedMesh = await parseMeshElement(child, defaultExtruderIndex, budget);
      if (parsedMesh) {
        meshes.push(parsedMesh);
      }
    }
  }

  for (const componentElement of Array.from(objectElement.getElementsByTagName('component'))) {
    await nextTick();

    const rawComponentPath = componentElement.getAttribute('p:path') || componentElement.getAttributeNS(PRODUCTION_NAMESPACE, 'path') || '';
    const componentPath = normalizeZipPath(rawComponentPath);
    const componentObjectId = componentElement.getAttribute('objectid');

    if (rawComponentPath && !componentPath) {
      console.warn(`Skipping invalid 3MF component path: ${rawComponentPath}`);
      continue;
    }

    if (!componentPath && !componentObjectId) {
      continue;
    }

    const componentReferenceKey = componentPath
      ? `${componentPath}#${componentObjectId ?? '*'}`
      : `${currentDocumentPath}#${componentObjectId}`;

    if (activeVisited.has(componentReferenceKey)) {
      console.warn(`Skipping circular 3MF component reference: ${componentReferenceKey}`);
      continue;
    }

    const partKey = componentObjectId ? `${objectId}:${componentObjectId}` : null;
    const componentExtruderIndex = partKey
      ? settings.partExtruders.get(partKey) ?? defaultExtruderIndex
      : defaultExtruderIndex;
    const componentVisited = new Set(activeVisited);
    componentVisited.add(componentReferenceKey);

    let componentMeshes: RawMeshData[] = [];
    if (!componentPath && componentObjectId) {
      componentMeshes = await parseComponentMeshes(
        currentDoc,
        currentDocumentPath,
        componentObjectId,
        zip,
        parser,
        settings,
        modelCache,
        objectCache,
        componentVisited,
        componentExtruderIndex,
        budget,
      );
    } else if (componentPath) {
      const componentDoc = await loadModelDocument(componentPath, zip, parser, modelCache);
      if (!componentDoc) {
        continue;
      }

      componentMeshes = await parseComponentMeshes(
        componentDoc,
        componentPath,
        componentObjectId,
        zip,
        parser,
        settings,
        modelCache,
        objectCache,
        componentVisited,
        componentExtruderIndex,
        budget,
      );
    }

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

  const parsedObject = meshes.length === 0
    ? null
    : {
      id: objectId,
      meshes,
      defaultExtruderIndex,
      plateId: parsePlateIdFromAttributes(objectElement) ?? settings.objectPlateIds.get(objectId) ?? null,
      name: objectName ?? null,
    } satisfies RawObjectData;

  objectCache.set(cacheKey, parsedObject);
  return parsedObject;
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

  validateZipEntrySizes(zip);

  const parser = new DOMParser();
  const settings = await parseModelSettings(zip, parser);
  const plateJsonData = await parsePlateJsonMetadata(zip);
  const mainModelPath = findMainModelPath(zip);

  if (!mainModelPath) {
    throw new Error('No model file found in the 3MF archive.');
  }

  const mainModelFile = zip.file(mainModelPath);
  if (!mainModelFile) {
    throw new Error('The 3MF archive is missing its main model file.');
  }

  const mainXml = await mainModelFile.async('string');
  assertXmlSize(mainXml);
  const mainDoc = parser.parseFromString(mainXml, 'application/xml');
  if (getXmlParserError(mainDoc)) {
    throw new Error('The 3MF model XML could not be parsed.');
  }

  const objectMap = new Map<string, RawObjectData>();
  const modelCache = new Map<string, Document | null>([[mainModelPath, mainDoc]]);
  const objectCache = new Map<string, RawObjectData | null>();
  const budget: ComplexityBudget = { totalVertices: 0, totalTriangles: 0 };

  for (const objectElement of Array.from(mainDoc.getElementsByTagName('object'))) {
    const parsedObject = await parseObjectMeshes(
      objectElement,
      zip,
      parser,
      settings,
      modelCache,
      objectCache,
      mainDoc,
      mainModelPath,
      undefined,
      budget,
    );
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
  let renderVertexCount = 0;
  let renderTriangleCount = 0;

  function chargeRenderBudget(vertexCount: number, triangleCount: number): void {
    renderVertexCount += vertexCount;
    renderTriangleCount += triangleCount;
    if (renderVertexCount > MAX_TOTAL_VERTICES) {
      throw new ThreeMfSecurityError(
        `The 3MF archive exceeds the render vertex limit of ${MAX_TOTAL_VERTICES.toLocaleString()} (repeated instances counted).`,
      );
    }
    if (renderTriangleCount > MAX_TOTAL_TRIANGLES) {
      throw new ThreeMfSecurityError(
        `The 3MF archive exceeds the render triangle limit of ${MAX_TOTAL_TRIANGLES.toLocaleString()} (repeated instances counted).`,
      );
    }
  }

  if (buildItems.length > MAX_RENDER_INSTANCES) {
    throw new ThreeMfSecurityError(`The 3MF archive contains more than ${MAX_RENDER_INSTANCES} build items.`);
  }

  if (buildItems.length > 0) {
    let buildItemIndex = 0;
    for (const buildItem of buildItems) {
      const objectData = objectMap.get(buildItem.objectId);
      if (!objectData) {
        continue;
      }

      for (const mesh of objectData.meshes) {
        chargeRenderBudget(mesh.vertices.length / 3, mesh.triangles.length / 3);
        renderableMeshes.push({
          objectId: buildItem.objectId,
          buildItemIndex,
          plateId: buildItem.plateId,
          extruderIndex: mesh.extruderIndex,
          geometry: createBufferGeometry(await transformVertices(mesh.vertices, buildItem.transform), mesh.triangles),
        });
      }
      buildItemIndex++;
    }
  } else {
    let buildItemIndex = 0;
    for (const objectData of objectMap.values()) {
      const plateId = objectData.name ? plateJsonData.objectPlateIdsByName.get(objectData.name) ?? objectData.plateId : objectData.plateId;
      for (const mesh of objectData.meshes) {
        chargeRenderBudget(mesh.vertices.length / 3, mesh.triangles.length / 3);
        renderableMeshes.push({
          objectId: objectData.id,
          buildItemIndex,
          plateId,
          extruderIndex: mesh.extruderIndex,
          geometry: createBufferGeometry(mesh.vertices, mesh.triangles),
        });
      }
      buildItemIndex++;
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
