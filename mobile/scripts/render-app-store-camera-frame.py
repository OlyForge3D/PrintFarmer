"""Render the DEBUG-only App Store printer-camera still (#3518).

The scene is built entirely from Blender primitives, so the image is project-owned
and license-clean. Run from mobile/:

    /Applications/Blender.app/Contents/MacOS/Blender -b -P scripts/render-app-store-camera-frame.py

Writes PrintFarmer/DebugAssets.xcassets/AppStoreCameraFrame.imageset/camera.jpg.
"""

import math
import os
from pathlib import Path

import bpy

OUTPUT = (Path(__file__).resolve().parents[1]
          / "PrintFarmer/DebugAssets.xcassets/AppStoreCameraFrame.imageset/camera.jpg")
PRINTED = 0.64


def material(name, color, roughness=0.5, metallic=0.0, bump=None, emission=None):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if emission:
        bsdf.inputs["Emission Color"].default_value = (*emission[0], 1)
        bsdf.inputs["Emission Strength"].default_value = emission[1]
    if bump:
        kind, scale, strength = bump
        coords = nodes.new("ShaderNodeTexCoord")
        if kind == "layers":
            texture = nodes.new("ShaderNodeTexWave")
            texture.wave_type = "BANDS"
            texture.bands_direction = "Z"
            texture.inputs["Scale"].default_value = scale
            texture.inputs["Distortion"].default_value = 0.4
            links.new(coords.outputs["Object"], texture.inputs["Vector"])
        else:
            texture = nodes.new("ShaderNodeTexNoise")
            texture.inputs["Scale"].default_value = scale
            texture.inputs["Detail"].default_value = 8
            links.new(coords.outputs["Object"], texture.inputs["Vector"])
        node = nodes.new("ShaderNodeBump")
        node.inputs["Strength"].default_value = strength
        links.new(texture.outputs["Fac"], node.inputs["Height"])
        links.new(node.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def box(name, size, location, mat, bevel=0.0, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.scale = size
    bpy.ops.object.transform_apply(scale=True)
    if bevel:
        modifier = obj.modifiers.new("bevel", "BEVEL")
        modifier.width = bevel
        modifier.segments = 4
    obj.data.materials.append(mat)
    bpy.ops.object.shade_smooth()
    return obj


def cylinder(name, radius, depth, location, mat, vertices=48, radius2=None, rotation=(0, 0, 0)):
    if radius2 is None:
        bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth,
                                            location=location, rotation=rotation)
    else:
        bpy.ops.mesh.primitive_cone_add(vertices=vertices, radius1=radius, radius2=radius2,
                                        depth=depth, location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    bpy.ops.object.shade_smooth()
    return obj


def clip_top(obj, height):
    """Cut a printed part flat at the current layer height."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, height + 0.5))
    cutter = bpy.context.object
    cutter.scale = (2, 2, 1)
    modifier = obj.modifiers.new("layer", "BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.object = cutter
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier="layer")
    bpy.data.objects.remove(cutter)


bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.samples = 256
scene.cycles.use_denoising = True
scene.render.resolution_x = 1280
scene.render.resolution_y = 960
scene.render.image_settings.file_format = "JPEG"
scene.render.image_settings.quality = 88
scene.view_settings.view_transform = "AgX"
scene.view_settings.look = "AgX - Medium High Contrast"
scene.world = bpy.data.worlds.new("enclosure")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.01, 0.011, 0.013, 1)

pla = material("PLA", (0.9, 0.24, 0.06), 0.35, bump=("layers", 260, 0.35))
pei = material("PEI", (0.11, 0.09, 0.05), 0.55, 0.65, bump=("noise", 380, 0.25))
steel = material("Steel", (0.55, 0.56, 0.58), 0.3, 1.0)
aluminium = material("Aluminium", (0.06, 0.06, 0.07), 0.45, 0.6)
plastic = material("Plastic", (0.035, 0.035, 0.04), 0.6)
silicone = material("Silicone", (0.02, 0.02, 0.022), 0.75)
brass = material("Brass", (0.85, 0.6, 0.25), 0.25, 1.0)
wall = material("Wall", (0.045, 0.047, 0.05), 0.85, bump=("noise", 40, 0.05))
led = material("LED", (1, 1, 1), emission=((1.0, 0.96, 0.9), 4))

# Build plate, frame and enclosure (metres; 0.25 m bed).
box("bed", (0.27, 0.27, 0.012), (0, 0, -0.006), aluminium, 0.002)
box("sheet", (0.255, 0.255, 0.0012), (0, 0, 0.0006), pei, 0.0005)
box("back wall", (0.6, 0.02, 0.5), (0, 0.2, 0.2), wall)
box("floor", (0.6, 0.6, 0.02), (0, 0, -0.07), wall)
for x in (-0.24, 0.24):
    box("wall", (0.02, 0.6, 0.5), (x, 0, 0.2), wall)
box("led strip", (0.4, 0.008, 0.004), (0, 0.15, 0.36), led)

# Tug-boat-style part, printed to 64% of its height.
height = 0.042
hull = box("hull", (0.07, 0.034, 0.02), (0.005, 0.0, 0.01), pla, 0.006)
taper = hull.modifiers.new("taper", "SIMPLE_DEFORM")
taper.deform_method = "TAPER"
taper.deform_axis = "Z"
taper.factor = 0.45
bpy.ops.object.modifier_apply(modifier="bevel")
box("cabin", (0.03, 0.026, 0.016), (-0.006, 0.0, 0.028), pla, 0.002)
cylinder("stack", 0.006, 0.016, (-0.006, 0.0, 0.042), pla)
for obj in [o for o in scene.objects if o.name in ("hull", "cabin", "stack")]:
    bpy.context.view_layer.objects.active = obj
    for modifier in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    clip_top(obj, height * PRINTED)
for obj in [o for o in scene.objects if o.name in ("stack",)]:
    if len(obj.data.vertices) == 0:
        bpy.data.objects.remove(obj)

# Skirt line around the part.
bpy.ops.mesh.primitive_torus_add(major_radius=0.058, minor_radius=0.0006, location=(0.005, 0, 0.0008))
skirt = bpy.context.object
skirt.scale = (1.0, 0.62, 0.7)
skirt.data.materials.append(pla)

# Toolhead just above the current layer, with the X gantry behind it.
top = height * PRINTED
head_x, head_y = 0.03, 0.006
cylinder("nozzle", 0.0035, 0.006, (head_x, head_y, top + 0.0045), brass, radius2=0.0008,
         rotation=(math.pi, 0, 0))
box("heater sock", (0.024, 0.02, 0.012), (head_x, head_y, top + 0.0135), silicone, 0.003)
box("toolhead", (0.05, 0.045, 0.06), (head_x, head_y + 0.004, top + 0.05), plastic, 0.006)
box("fan duct", (0.018, 0.02, 0.016), (head_x - 0.03, head_y - 0.004, top + 0.018), plastic, 0.004)
box("x gantry", (0.46, 0.03, 0.03), (0, head_y + 0.04, top + 0.06), aluminium, 0.002)
cylinder("x rod", 0.004, 0.46, (0, head_y + 0.022, top + 0.045), steel, rotation=(0, math.pi / 2, 0))

# Lighting: enclosure LED plus a dim fill from the camera side.
bpy.ops.object.light_add(type="AREA", location=(0, 0.1, 0.34))
key = bpy.context.object
key.data.energy = 5
key.data.size = 0.35
key.data.color = (1.0, 0.95, 0.88)
bpy.ops.object.light_add(type="AREA", location=(0, -0.3, 0.2), rotation=(math.radians(60), 0, 0))
fill = bpy.context.object
fill.data.energy = 0.6
fill.data.size = 0.4

# Wide-angle enclosure camera, front left, looking down at the plate.
bpy.ops.object.camera_add(location=(-0.045, -0.16, 0.105))
camera = bpy.context.object
camera.data.lens = 20
camera.data.dof.use_dof = True
camera.data.dof.aperture_fstop = 2.8
target = bpy.data.objects.new("target", None)
target.location = (0.01, 0.0, 0.012)
scene.collection.objects.link(target)
camera.data.dof.focus_object = target
constraint = camera.constraints.new("TRACK_TO")
constraint.target = target
scene.camera = camera

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
scene.render.filepath = str(OUTPUT)
bpy.ops.render.render(write_still=True)
print(f"Wrote {OUTPUT} ({os.path.getsize(OUTPUT)} bytes)")
