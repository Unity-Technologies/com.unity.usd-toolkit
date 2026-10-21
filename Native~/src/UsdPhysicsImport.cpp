#include "UsdPhysicsImport.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <functional>
#include <limits>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <pxr/base/gf/quatd.h>
#include <pxr/base/gf/quatf.h>
#include <pxr/base/gf/vec3d.h>
#include <pxr/base/gf/vec3f.h>
#include <pxr/base/tf/span.h>
#include <pxr/base/tf/stringUtils.h>
#include <pxr/base/tf/token.h>
#include <pxr/base/vt/array.h>
#include <pxr/base/vt/value.h>
#include <pxr/usd/sdf/listOp.h>
#include <pxr/usd/sdf/path.h>
#include <pxr/usd/usd/attribute.h>
#include <pxr/usd/usd/prim.h>
#include <pxr/usd/usd/tokens.h>
#include <pxr/usd/usdGeom/mesh.h>
#include <pxr/usd/usdGeom/metrics.h>
#include <pxr/usd/usdGeom/tokens.h>
#include <pxr/usd/usdPhysics/metrics.h>
#include <pxr/usd/usdPhysics/parseDesc.h>
#include <pxr/usd/usdPhysics/parseUtils.h>

PXR_NAMESPACE_USING_DIRECTIVE

namespace rusd_physics
{

struct Store
{
    RUsdPhysicsData data{};
    std::vector<RUsdPhysicsScene> scenes;
    std::vector<RUsdPhysicsBody> bodies;
    std::vector<RUsdPhysicsCollider> colliders;
    std::vector<RUsdPhysicsMaterial> materials;
    std::vector<RUsdPhysicsJoint> joints;
    std::vector<RUsdPhysicsArticulation> articulations;
    std::vector<RUsdPhysicsCollisionGroup> groups;
    std::vector<RUsdPhysicsUnsupported> unsupported;
    std::vector<int> indices;
    std::vector<float> floats;
    std::vector<char> strings;
    std::vector<int> stringOffsets;
};

void StoreDeleter::operator()(Store* store) const
{
    delete store;
}

const RUsdPhysicsData* Data(const Store& store)
{
    return &store.data;
}

namespace
{

constexpr float kInfinity = std::numeric_limits<float>::infinity();

RUsdVec3 ToVec3(const GfVec3f& value)
{
    return RUsdVec3{value[0], value[1], value[2]};
}

// GfQuatf is real-first; the API is x, y, z, w.
RUsdVec4 ToQuat(const GfQuatf& value)
{
    const GfVec3f& imaginary = value.GetImaginary();
    return RUsdVec4{imaginary[0], imaginary[1], imaginary[2], value.GetReal()};
}

int ToAxis(UsdPhysicsAxis axis)
{
    switch (axis)
    {
    case UsdPhysicsAxis::X: return RUSD_PHYSICS_AXIS_X;
    case UsdPhysicsAxis::Y: return RUSD_PHYSICS_AXIS_Y;
    default: return RUSD_PHYSICS_AXIS_Z;
    }
}

int ToDof(UsdPhysicsJointDOF dof)
{
    switch (dof)
    {
    case UsdPhysicsJointDOF::TransX: return RUSD_PHYSICS_DOF_TRANS_X;
    case UsdPhysicsJointDOF::TransY: return RUSD_PHYSICS_DOF_TRANS_Y;
    case UsdPhysicsJointDOF::TransZ: return RUSD_PHYSICS_DOF_TRANS_Z;
    case UsdPhysicsJointDOF::RotX: return RUSD_PHYSICS_DOF_ROT_X;
    case UsdPhysicsJointDOF::RotY: return RUSD_PHYSICS_DOF_ROT_Y;
    case UsdPhysicsJointDOF::RotZ: return RUSD_PHYSICS_DOF_ROT_Z;
    case UsdPhysicsJointDOF::Distance: return RUSD_PHYSICS_DOF_DISTANCE;
    default: return -1;
    }
}

// The instance name UsdPhysics and PhysX give each DOF's multiple-apply schemas (drive:<name>,
// state:<name>, physxJointAxis:<name>). Revolute and prismatic joints use "angular" and "linear".
const char* DofInstanceName(int dof)
{
    static const char* const names[RUSD_PHYSICS_DOF_COUNT] = {
        "transX", "transY", "transZ", "rotX", "rotY", "rotZ", "distance"};
    return dof >= 0 && dof < RUSD_PHYSICS_DOF_COUNT ? names[dof] : "";
}

int ToApproximation(const TfToken& token)
{
    const std::string& text = token.GetString();
    if (text.empty() || text == "none") return RUSD_PHYSICS_MESH_NONE;
    if (text == "convexHull") return RUSD_PHYSICS_MESH_CONVEX_HULL;
    if (text == "convexDecomposition") return RUSD_PHYSICS_MESH_CONVEX_DECOMPOSITION;
    if (text == "boundingSphere") return RUSD_PHYSICS_MESH_BOUNDING_SPHERE;
    if (text == "boundingCube") return RUSD_PHYSICS_MESH_BOUNDING_CUBE;
    if (text == "meshSimplification") return RUSD_PHYSICS_MESH_SIMPLIFICATION;
    if (text == "sdf") return RUSD_PHYSICS_MESH_SDF;
    return RUSD_PHYSICS_MESH_OTHER;
}

// ---- Authored attributes, read by name ------------------------------------------------------
// PhysxSchema is not registered in this build, so its attributes are untyped as far as the schema
// registry knows; and a VtValue's IsHolding<T> compares typeid, which does not match across the
// plugin / OpenUSD boundary (BUILD_NOTES). Values are read as VtValue and told apart by type name,
// which also takes the int / uint / float / double variants different exporters author.

bool ReadAuthored(const UsdPrim& prim, const char* name, VtValue* value)
{
    const UsdAttribute attribute = prim.GetAttribute(TfToken(name));
    return attribute && attribute.HasAuthoredValue() && attribute.Get(value, UsdTimeCode::Default());
}

bool ReadNumber(const UsdPrim& prim, const char* name, double* out)
{
    VtValue value;
    if (!ReadAuthored(prim, name, &value))
    {
        return false;
    }

    const std::string type = value.GetTypeName();
    if (type == "float") *out = value.UncheckedGet<float>();
    else if (type == "double") *out = value.UncheckedGet<double>();
    else if (type == "int") *out = value.UncheckedGet<int>();
    else if (type == "unsigned int") *out = value.UncheckedGet<unsigned int>();
    else if (type == "bool") *out = value.UncheckedGet<bool>() ? 1.0 : 0.0;
    else return false;

    return !std::isnan(*out);
}

bool ReadFloat(const UsdPrim& prim, const char* name, float* out)
{
    double value = 0.0;
    if (!ReadNumber(prim, name, &value))
    {
        return false;
    }

    *out = static_cast<float>(value);
    return true;
}

bool ReadInt(const UsdPrim& prim, const char* name, int* out)
{
    double value = 0.0;
    if (!ReadNumber(prim, name, &value) || !std::isfinite(value))
    {
        return false;
    }

    const double clamped = std::fmax(std::fmin(value, double(std::numeric_limits<int>::max())),
                                     double(std::numeric_limits<int>::min()));
    *out = static_cast<int>(clamped);
    return true;
}

bool ReadVec3(const UsdPrim& prim, const char* name, RUsdVec3* out)
{
    VtValue value;
    if (!ReadAuthored(prim, name, &value))
    {
        return false;
    }

    GfVec3f vector;
    const std::string type = value.GetTypeName();
    if (type == "GfVec3f") vector = value.UncheckedGet<GfVec3f>();
    else if (type == "GfVec3d") vector = GfVec3f(value.UncheckedGet<GfVec3d>());
    else return false;

    // MassAPI's fallbacks are sentinels (-inf centre of mass, zero inertia): only a finite value counts.
    if (!std::isfinite(vector[0]) || !std::isfinite(vector[1]) || !std::isfinite(vector[2]))
    {
        return false;
    }

    *out = ToVec3(vector);
    return true;
}

bool ReadQuat(const UsdPrim& prim, const char* name, RUsdVec4* out)
{
    VtValue value;
    if (!ReadAuthored(prim, name, &value))
    {
        return false;
    }

    GfQuatf quat;
    const std::string type = value.GetTypeName();
    if (type == "GfQuatf") quat = value.UncheckedGet<GfQuatf>();
    else if (type == "GfQuatd") quat = GfQuatf(value.UncheckedGet<GfQuatd>());
    else return false;

    if (quat.GetLength() == 0.0f)
    {
        return false;
    }

    *out = ToQuat(quat.GetNormalized());
    return true;
}

bool ReadToken(const UsdPrim& prim, const char* name, std::string* out)
{
    VtValue value;
    if (!ReadAuthored(prim, name, &value) || value.GetTypeName() != "TfToken")
    {
        return false;
    }

    *out = value.UncheckedGet<TfToken>().GetString();
    return true;
}

template <typename T>
void ReadInto(bool (*read)(const UsdPrim&, const char*, T*), const UsdPrim& prim, const char* name,
              T* field, unsigned int* mask, unsigned int bit)
{
    if (read(prim, name, field))
    {
        *mask |= bit;
    }
}

bool ReadCombineMode(const UsdPrim& prim, const char* name, int* out)
{
    std::string mode;
    if (!ReadToken(prim, name, &mode))
    {
        return false;
    }

    if (mode == "average") *out = RUSD_PHYSICS_COMBINE_AVERAGE;
    else if (mode == "min") *out = RUSD_PHYSICS_COMBINE_MIN;
    else if (mode == "multiply") *out = RUSD_PHYSICS_COMBINE_MULTIPLY;
    else if (mode == "max") *out = RUSD_PHYSICS_COMBINE_MAX;
    else return false;
    return true;
}

// An applied API schema whose name starts with `prefix` (PhysxMimicJointAPI:rotX, ...).
// UsdPrim::GetAppliedSchemas() leaves out schemas this build does not register, which every PhysX
// one is, so the composed apiSchemas list op is read instead (by type name, like any VtValue here).
std::string FindAppliedSchema(const UsdPrim& prim, const char* prefix)
{
    VtValue value;
    if (!prim.GetMetadata(UsdTokens->apiSchemas, &value) || value.GetTypeName() != "SdfListOp<TfToken>")
    {
        return std::string();
    }

    for (const TfToken& schema : value.UncheckedGet<SdfTokenListOp>().GetAppliedItems())
    {
        if (TfStringStartsWith(schema.GetString(), prefix))
        {
            return schema.GetString();
        }
    }
    return std::string();
}

// ---- What the parser reports, kept until every object is known --------------------------------

struct ShapeEntry
{
    ShapeEntry(const SdfPath& inPath, const UsdPhysicsShapeDesc& inDesc) : path(inPath), desc(inDesc) {}

    SdfPath path;
    UsdPhysicsShapeDesc desc;   // the common part, sliced off the concrete descriptor
    RUsdPhysicsCollider record{};
    std::vector<UsdPhysicsSpherePoint> spheres;
};

struct JointEntry
{
    JointEntry(const SdfPath& inPath, const UsdPhysicsJointDesc& inDesc) : path(inPath), desc(inDesc) {}

    SdfPath path;
    UsdPhysicsJointDesc desc;   // the common part
    RUsdPhysicsJoint record{};
    int primaryDof = -1;        // the DOF a revolute or prismatic joint moves along
};

class Reader
{
public:
    Reader(const UsdStageRefPtr& stage, const RUsdPhysicsReadOptions& options,
           const ImportLookups& lookups, Store* store)
        : _stage(stage), _options(options), _lookups(lookups), _store(store),
          _readPhysx((options.flags & RUSD_PHYSICS_READ_PHYSX) != 0)
    {
    }

    void Run();

private:
    void Report(UsdPhysicsObjectType type, TfSpan<const SdfPath> paths,
                TfSpan<const UsdPhysicsObjectDesc> descs);
    template <typename Desc>
    void ForEach(TfSpan<const SdfPath> paths, TfSpan<const UsdPhysicsObjectDesc> descs,
                 const std::function<void(const SdfPath&, const Desc&)>& body);
    bool Admit(const SdfPath& path, const UsdPhysicsObjectDesc& desc);
    void AddShape(const SdfPath& path, const UsdPhysicsShapeDesc& desc, int shape);
    void AddJoint(const SdfPath& path, const UsdPhysicsJointDesc& desc, int type);

    void Resolve();
    void ResolveBodies();
    void ResolveColliders();
    void ResolveMaterials();
    void ResolveJoints();
    void ResolveArticulations();
    void ResolveGroups();
    void ReadMesh(const SdfPath& path, RUsdPhysicsCollider* record);
    void ReadJointPhysx(const UsdPrim& prim, JointEntry* entry);
    void Finish();

    int String(const std::string& text);
    int PathString(const SdfPath& path) { return path.IsEmpty() ? -1 : String(path.GetString()); }
    int Node(const SdfPath& path) const { return _lookups.node ? _lookups.node(path.GetString()) : -1; }
    void Unsupported(const SdfPath& path, int kind, const std::string& detail);
    void LimitHit(const char* what);
    RUsdPhysicsList PathList(const SdfPathVector& paths);
    RUsdPhysicsList IndexList(const SdfPathVector& paths,
                              const std::unordered_map<SdfPath, int, SdfPath::Hash>& map);

    UsdStageRefPtr _stage;
    const RUsdPhysicsReadOptions& _options;
    const ImportLookups& _lookups;
    Store* _store;
    bool _readPhysx;

    long long _objects = 0;
    long long _meshElements = 0;
    std::unordered_set<std::string> _limitsHit;
    std::unordered_map<std::string, int> _strings;

    std::vector<std::pair<SdfPath, UsdPhysicsSceneDesc>> _scenes;
    std::vector<std::pair<SdfPath, UsdPhysicsRigidBodyDesc>> _bodies;
    std::vector<ShapeEntry> _shapes;
    std::vector<std::pair<SdfPath, UsdPhysicsRigidBodyMaterialDesc>> _materials;
    std::vector<JointEntry> _joints;
    std::vector<std::pair<SdfPath, UsdPhysicsArticulationDesc>> _articulations;
    std::vector<std::pair<SdfPath, UsdPhysicsCollisionGroupDesc>> _groups;

    std::unordered_map<SdfPath, int, SdfPath::Hash> _bodyIndex;
    std::unordered_map<SdfPath, int, SdfPath::Hash> _shapeIndex;
    std::unordered_map<SdfPath, int, SdfPath::Hash> _materialIndex;
    std::unordered_map<SdfPath, int, SdfPath::Hash> _jointIndex;
    std::unordered_map<SdfPath, int, SdfPath::Hash> _groupIndex;
};

int Reader::String(const std::string& text)
{
    const auto found = _strings.find(text);
    if (found != _strings.end())
    {
        return found->second;
    }

    const int index = static_cast<int>(_store->stringOffsets.size());
    _store->stringOffsets.push_back(static_cast<int>(_store->strings.size()));
    _store->strings.insert(_store->strings.end(), text.begin(), text.end());
    _store->strings.push_back('\0');
    _strings.emplace(text, index);
    return index;
}

void Reader::Unsupported(const SdfPath& path, int kind, const std::string& detail)
{
    _store->unsupported.push_back(
        RUsdPhysicsUnsupported{PathString(path), kind, detail.empty() ? -1 : String(detail)});
}

void Reader::LimitHit(const char* what)
{
    if (_limitsHit.insert(what).second)
    {
        Unsupported(SdfPath(), RUSD_PHYSICS_UNSUPPORTED_LIMIT, what);
    }
}

RUsdPhysicsList Reader::PathList(const SdfPathVector& paths)
{
    RUsdPhysicsList list{static_cast<int>(_store->indices.size()), 0};
    for (const SdfPath& path : paths)
    {
        if (!path.IsEmpty())
        {
            _store->indices.push_back(String(path.GetString()));
            ++list.count;
        }
    }
    return list;
}

RUsdPhysicsList Reader::IndexList(const SdfPathVector& paths,
                                  const std::unordered_map<SdfPath, int, SdfPath::Hash>& map)
{
    RUsdPhysicsList list{static_cast<int>(_store->indices.size()), 0};
    for (const SdfPath& path : paths)
    {
        const auto found = map.find(path);
        if (found != map.end())
        {
            _store->indices.push_back(found->second);
            ++list.count;
        }
    }
    return list;
}

// ---- Collecting --------------------------------------------------------------------------------

// The parser hands each batch over as a span of the base descriptor, but the memory behind it is an
// array of the concrete type (parseUtils.cpp, _CallReportFn): stepping through the span itself would
// read every element after the first at the wrong offset.
template <typename Desc>
void Reader::ForEach(TfSpan<const SdfPath> paths, TfSpan<const UsdPhysicsObjectDesc> descs,
                     const std::function<void(const SdfPath&, const Desc&)>& body)
{
    const Desc* typed = static_cast<const Desc*>(descs.data());
    const size_t count = std::min(paths.size(), descs.size());
    for (size_t i = 0; i < count; ++i)
    {
        if (Admit(paths[i], typed[i]))
        {
            body(paths[i], typed[i]);
        }
    }
}

bool Reader::Admit(const SdfPath& path, const UsdPhysicsObjectDesc& desc)
{
    if (++_objects > _options.maxObjects)
    {
        if (!_options.skipWhenExceeded)
        {
            throw std::runtime_error("it has more than " +
                                     std::to_string(_options.maxObjects) +
                                     " physics objects (RUsdPhysicsReadOptions.maxObjects).");
        }
        LimitHit("maxObjects");
        return false;
    }

    if (!desc.isValid)
    {
        Unsupported(path, RUSD_PHYSICS_UNSUPPORTED_INVALID, std::string());
        return false;
    }
    return true;
}

void Reader::AddShape(const SdfPath& path, const UsdPhysicsShapeDesc& desc, int shape)
{
    ShapeEntry entry(path, desc);
    entry.record.shape = shape;
    entry.record.axis = RUSD_PHYSICS_AXIS_Z;
    entry.record.meshIndex = -1;
    entry.record.meshScale = RUsdVec3{1.0f, 1.0f, 1.0f};
    _shapes.push_back(std::move(entry));
}

void Reader::AddJoint(const SdfPath& path, const UsdPhysicsJointDesc& desc, int type)
{
    JointEntry entry(path, desc);
    entry.record.type = type;
    entry.record.axis = RUSD_PHYSICS_AXIS_X;
    for (RUsdPhysicsJointDof& dof : entry.record.dofs)
    {
        dof.maxForce = kInfinity;
    }
    _joints.push_back(std::move(entry));
}

void CopyLimit(const UsdPhysicsJointLimit& limit, RUsdPhysicsJointDof* dof)
{
    dof->limitEnabled = limit.enabled ? 1 : 0;
    dof->lower = limit.lower;
    dof->upper = limit.upper;
}

void CopyDrive(const UsdPhysicsJointDrive& drive, RUsdPhysicsJointDof* dof)
{
    dof->driveEnabled = drive.enabled ? 1 : 0;
    dof->driveIsAcceleration = drive.acceleration ? 1 : 0;
    dof->targetPosition = drive.targetPosition;
    dof->targetVelocity = drive.targetVelocity;
    dof->maxForce = drive.forceLimit;
    dof->stiffness = drive.stiffness;
    dof->damping = drive.damping;
}

void Reader::Report(UsdPhysicsObjectType type, TfSpan<const SdfPath> paths,
                    TfSpan<const UsdPhysicsObjectDesc> descs)
{
    using T = UsdPhysicsObjectType;
    switch (type)
    {
    case T::Scene:
        ForEach<UsdPhysicsSceneDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsSceneDesc& d) {
            _scenes.emplace_back(path, d);
        });
        break;
    case T::RigidBody:
        ForEach<UsdPhysicsRigidBodyDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsRigidBodyDesc& d) {
            _bodies.emplace_back(path, d);
        });
        break;
    case T::SphereShape:
        ForEach<UsdPhysicsSphereShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsSphereShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_SPHERE);
            _shapes.back().record.radius0 = _shapes.back().record.radius1 = d.radius;
        });
        break;
    case T::CubeShape:
        ForEach<UsdPhysicsCubeShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCubeShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CUBE);
            _shapes.back().record.halfExtents = ToVec3(d.halfExtents);
        });
        break;
    case T::CapsuleShape:
        ForEach<UsdPhysicsCapsuleShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCapsuleShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CAPSULE);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.radius0 = r.radius1 = d.radius;
            r.halfHeight = d.halfHeight;
            r.axis = ToAxis(d.axis);
        });
        break;
    case T::Capsule1Shape:
        ForEach<UsdPhysicsCapsule1ShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCapsule1ShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CAPSULE_TAPERED);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.radius0 = d.topRadius;
            r.radius1 = d.bottomRadius;
            r.halfHeight = d.halfHeight;
            r.axis = ToAxis(d.axis);
        });
        break;
    case T::CylinderShape:
        ForEach<UsdPhysicsCylinderShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCylinderShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CYLINDER);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.radius0 = r.radius1 = d.radius;
            r.halfHeight = d.halfHeight;
            r.axis = ToAxis(d.axis);
        });
        break;
    case T::Cylinder1Shape:
        ForEach<UsdPhysicsCylinder1ShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCylinder1ShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CYLINDER_TAPERED);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.radius0 = d.topRadius;
            r.radius1 = d.bottomRadius;
            r.halfHeight = d.halfHeight;
            r.axis = ToAxis(d.axis);
        });
        break;
    case T::ConeShape:
        ForEach<UsdPhysicsConeShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsConeShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_CONE);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.radius0 = r.radius1 = d.radius;
            r.halfHeight = d.halfHeight;
            r.axis = ToAxis(d.axis);
        });
        break;
    case T::PlaneShape:
        ForEach<UsdPhysicsPlaneShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsPlaneShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_PLANE);
            _shapes.back().record.axis = ToAxis(d.axis);
        });
        break;
    case T::MeshShape:
        ForEach<UsdPhysicsMeshShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsMeshShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_MESH);
            RUsdPhysicsCollider& r = _shapes.back().record;
            r.meshApproximation = ToApproximation(d.approximation);
            r.meshScale = ToVec3(d.meshScale);
            r.doubleSided = d.doubleSided ? 1 : 0;
        });
        break;
    case T::SpherePointsShape:
        ForEach<UsdPhysicsSpherePointsShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsSpherePointsShapeDesc& d) {
            AddShape(path, d, RUSD_PHYSICS_SHAPE_SPHERE_POINTS);
            _shapes.back().spheres = d.spherePoints;
        });
        break;
    case T::CustomShape:
        ForEach<UsdPhysicsCustomShapeDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCustomShapeDesc& d) {
            Unsupported(path, RUSD_PHYSICS_UNSUPPORTED_CUSTOM_SHAPE, d.customGeometryToken.GetString());
        });
        break;
    case T::FixedJoint:
        ForEach<UsdPhysicsFixedJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsFixedJointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_FIXED);
        });
        break;
    case T::RevoluteJoint:
        ForEach<UsdPhysicsRevoluteJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsRevoluteJointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_REVOLUTE);
            JointEntry& e = _joints.back();
            e.record.axis = ToAxis(d.axis);
            e.primaryDof = RUSD_PHYSICS_DOF_ROT_X + e.record.axis;
            CopyLimit(d.limit, &e.record.dofs[e.primaryDof]);
            CopyDrive(d.drive, &e.record.dofs[e.primaryDof]);
        });
        break;
    case T::PrismaticJoint:
        ForEach<UsdPhysicsPrismaticJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsPrismaticJointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_PRISMATIC);
            JointEntry& e = _joints.back();
            e.record.axis = ToAxis(d.axis);
            e.primaryDof = RUSD_PHYSICS_DOF_TRANS_X + e.record.axis;
            CopyLimit(d.limit, &e.record.dofs[e.primaryDof]);
            CopyDrive(d.drive, &e.record.dofs[e.primaryDof]);
        });
        break;
    case T::SphericalJoint:
        ForEach<UsdPhysicsSphericalJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsSphericalJointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_SPHERICAL);
            RUsdPhysicsJoint& r = _joints.back().record;
            r.axis = ToAxis(d.axis);
            r.coneLimitEnabled = d.limit.enabled ? 1 : 0;
            r.coneAngle0 = d.limit.angle0;
            r.coneAngle1 = d.limit.angle1;
        });
        break;
    case T::DistanceJoint:
        ForEach<UsdPhysicsDistanceJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsDistanceJointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_DISTANCE);
            RUsdPhysicsJointDof& dof = _joints.back().record.dofs[RUSD_PHYSICS_DOF_DISTANCE];
            dof.limitEnabled = (d.minEnabled || d.maxEnabled) ? 1 : 0;
            dof.lower = d.minEnabled ? d.limit.minDist : 0.0f;
            dof.upper = d.maxEnabled ? d.limit.maxDist : kInfinity;
        });
        break;
    case T::D6Joint:
        ForEach<UsdPhysicsD6JointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsD6JointDesc& d) {
            AddJoint(path, d, RUSD_PHYSICS_JOINT_D6);
            RUsdPhysicsJoint& r = _joints.back().record;
            for (const auto& limit : d.jointLimits)
            {
                const int dof = ToDof(limit.first);
                if (dof >= 0) CopyLimit(limit.second, &r.dofs[dof]);
            }
            for (const auto& drive : d.jointDrives)
            {
                const int dof = ToDof(drive.first);
                if (dof >= 0) CopyDrive(drive.second, &r.dofs[dof]);
            }
        });
        break;
    case T::CustomJoint:
        ForEach<UsdPhysicsCustomJointDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCustomJointDesc&) {
            const UsdPrim prim = _stage->GetPrimAtPath(path);
            Unsupported(path, RUSD_PHYSICS_UNSUPPORTED_CUSTOM_JOINT, prim ? prim.GetTypeName().GetString() : std::string());
        });
        break;
    case T::RigidBodyMaterial:
        ForEach<UsdPhysicsRigidBodyMaterialDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsRigidBodyMaterialDesc& d) {
            _materials.emplace_back(path, d);
        });
        break;
    case T::Articulation:
        ForEach<UsdPhysicsArticulationDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsArticulationDesc& d) {
            _articulations.emplace_back(path, d);
        });
        break;
    case T::CollisionGroup:
        ForEach<UsdPhysicsCollisionGroupDesc>(paths, descs, [&](const SdfPath& path, const UsdPhysicsCollisionGroupDesc& d) {
            _groups.emplace_back(path, d);
        });
        break;
    default:
        break;
    }
}

// ---- Resolving ---------------------------------------------------------------------------------

void Reader::ResolveBodies()
{
    for (const auto& item : _bodies)
    {
        const SdfPath& path = item.first;
        const UsdPhysicsRigidBodyDesc& d = item.second;
        RUsdPhysicsBody r{};
        r.pathIndex = PathString(path);
        r.nodeIndex = Node(path);
        r.articulationIndex = -1;
        r.position = ToVec3(d.position);
        r.rotation = ToQuat(d.rotation);
        r.scale = ToVec3(d.scale);
        r.linearVelocity = ToVec3(d.linearVelocity);
        r.angularVelocity = ToVec3(d.angularVelocity);
        r.enabled = d.rigidBodyEnabled ? 1 : 0;
        r.kinematic = d.kinematicBody ? 1 : 0;
        r.startsAsleep = d.startsAsleep ? 1 : 0;
        r.colliders = IndexList(d.collisions, _shapeIndex);
        r.filtered = PathList(d.filteredCollisions);

        const UsdPrim prim = _stage->GetPrimAtPath(path);
        if (prim)
        {
            ReadInto(ReadFloat, prim, "physics:mass", &r.mass, &r.setMask, RUSD_PHYSICS_BODY_SET_MASS);
            ReadInto(ReadFloat, prim, "physics:density", &r.density, &r.setMask, RUSD_PHYSICS_BODY_SET_DENSITY);
            ReadInto(ReadVec3, prim, "physics:centerOfMass", &r.centerOfMass, &r.setMask, RUSD_PHYSICS_BODY_SET_CENTER_OF_MASS);
            ReadInto(ReadVec3, prim, "physics:diagonalInertia", &r.diagonalInertia, &r.setMask, RUSD_PHYSICS_BODY_SET_INERTIA);
            ReadInto(ReadQuat, prim, "physics:principalAxes", &r.principalAxes, &r.setMask, RUSD_PHYSICS_BODY_SET_PRINCIPAL_AXES);
            if (_readPhysx)
            {
                ReadInto(ReadFloat, prim, "physxRigidBody:linearDamping", &r.linearDamping, &r.setMask, RUSD_PHYSICS_BODY_SET_LINEAR_DAMPING);
                ReadInto(ReadFloat, prim, "physxRigidBody:angularDamping", &r.angularDamping, &r.setMask, RUSD_PHYSICS_BODY_SET_ANGULAR_DAMPING);
                ReadInto(ReadFloat, prim, "physxRigidBody:maxLinearVelocity", &r.maxLinearVelocity, &r.setMask, RUSD_PHYSICS_BODY_SET_MAX_LINEAR_VELOCITY);
                ReadInto(ReadFloat, prim, "physxRigidBody:maxAngularVelocity", &r.maxAngularVelocity, &r.setMask, RUSD_PHYSICS_BODY_SET_MAX_ANGULAR_VELOCITY);
                ReadInto(ReadFloat, prim, "physxRigidBody:maxDepenetrationVelocity", &r.maxDepenetrationVelocity, &r.setMask, RUSD_PHYSICS_BODY_SET_MAX_DEPENETRATION);
                ReadInto(ReadFloat, prim, "physxRigidBody:sleepThreshold", &r.sleepThreshold, &r.setMask, RUSD_PHYSICS_BODY_SET_SLEEP_THRESHOLD);
                ReadInto(ReadInt, prim, "physxRigidBody:solverPositionIterationCount", &r.solverPositionIterations, &r.setMask, RUSD_PHYSICS_BODY_SET_POSITION_ITERATIONS);
                ReadInto(ReadInt, prim, "physxRigidBody:solverVelocityIterationCount", &r.solverVelocityIterations, &r.setMask, RUSD_PHYSICS_BODY_SET_VELOCITY_ITERATIONS);
                ReadInto(ReadInt, prim, "physxRigidBody:enableCCD", &r.enableCCD, &r.setMask, RUSD_PHYSICS_BODY_SET_CCD);
                ReadInto(ReadInt, prim, "physxRigidBody:disableGravity", &r.disableGravity, &r.setMask, RUSD_PHYSICS_BODY_SET_DISABLE_GRAVITY);

                const std::string tendon = FindAppliedSchema(prim, "PhysxTendon");
                if (!tendon.empty())
                {
                    Unsupported(path, RUSD_PHYSICS_UNSUPPORTED_TENDON, tendon);
                }
            }
        }
        _store->bodies.push_back(r);
    }
}

void Reader::ReadMesh(const SdfPath& path, RUsdPhysicsCollider* record)
{
    const UsdGeomMesh mesh(_stage->GetPrimAtPath(path));
    VtVec3fArray points;
    VtIntArray counts;
    VtIntArray faceIndices;
    if (!mesh || !mesh.GetPointsAttr().Get(&points, UsdTimeCode::Default()) ||
        !mesh.GetFaceVertexCountsAttr().Get(&counts, UsdTimeCode::Default()) ||
        !mesh.GetFaceVertexIndicesAttr().Get(&faceIndices, UsdTimeCode::Default()) ||
        points.empty())
    {
        Unsupported(path, RUSD_PHYSICS_UNSUPPORTED_MESH, "points, faceVertexCounts, faceVertexIndices");
        return;
    }

    // A fan triangulation of n vertices has 3(n-2) indices, at most 3 per face-vertex index.
    const long long elements = static_cast<long long>(points.size()) * 3 +
                               static_cast<long long>(faceIndices.size()) * 3;
    if (_meshElements + elements > _options.maxMeshElements)
    {
        if (!_options.skipWhenExceeded)
        {
            throw std::runtime_error("collision meshes exceed " +
                                     std::to_string(_options.maxMeshElements) +
                                     " points and indices (RUsdPhysicsReadOptions.maxMeshElements).");
        }
        LimitHit("maxMeshElements");
        return;
    }
    _meshElements += elements;

    record->meshPoints.offset = static_cast<int>(_store->floats.size());
    record->meshPoints.count = static_cast<int>(points.size());
    for (const GfVec3f& point : points)
    {
        _store->floats.insert(_store->floats.end(), {point[0], point[1], point[2]});
    }

    // Faces whose counts or indices do not fit the arrays are dropped, not trusted.
    record->meshTriangles.offset = static_cast<int>(_store->indices.size());
    const size_t pointCount = points.size();
    size_t start = 0;
    for (const int count : counts)
    {
        if (count < 0 || start + static_cast<size_t>(count) > faceIndices.size())
        {
            break;
        }

        bool valid = count >= 3;
        for (int i = 0; valid && i < count; ++i)
        {
            const int index = faceIndices[start + i];
            valid = index >= 0 && static_cast<size_t>(index) < pointCount;
        }

        if (valid)
        {
            for (int i = 1; i + 1 < count; ++i)
            {
                _store->indices.insert(_store->indices.end(),
                                       {faceIndices[start], faceIndices[start + i], faceIndices[start + i + 1]});
                record->meshTriangles.count += 3;
            }
        }
        start += static_cast<size_t>(count);
    }
}

void Reader::ResolveColliders()
{
    for (ShapeEntry& entry : _shapes)
    {
        RUsdPhysicsCollider& r = entry.record;
        const UsdPhysicsShapeDesc& d = entry.desc;
        r.pathIndex = PathString(entry.path);
        r.nodeIndex = Node(entry.path);
        const auto body = _bodyIndex.find(d.rigidBody);
        r.bodyIndex = body != _bodyIndex.end() ? body->second : -1;
        r.localPosition = ToVec3(d.localPos);
        r.localRotation = ToQuat(d.localRot);
        r.localScale = ToVec3(d.localScale);
        r.enabled = d.collisionEnabled ? 1 : 0;
        r.materials = IndexList(d.materials, _materialIndex);
        r.groups = IndexList(d.collisionGroups, _groupIndex);
        r.filtered = PathList(d.filteredCollisions);

        if (r.shape == RUSD_PHYSICS_SHAPE_MESH)
        {
            r.meshIndex = _lookups.mesh ? _lookups.mesh(entry.path.GetString()) : -1;
            ReadMesh(entry.path, &r);
        }
        else if (r.shape == RUSD_PHYSICS_SHAPE_SPHERE_POINTS)
        {
            r.spherePoints.offset = static_cast<int>(_store->floats.size());
            for (const UsdPhysicsSpherePoint& sphere : entry.spheres)
            {
                _store->floats.insert(_store->floats.end(),
                                      {sphere.center[0], sphere.center[1], sphere.center[2], sphere.radius});
                ++r.spherePoints.count;
            }
        }

        const UsdPrim prim = _stage->GetPrimAtPath(entry.path);
        if (prim)
        {
            ReadInto(ReadFloat, prim, "physics:mass", &r.mass, &r.setMask, RUSD_PHYSICS_COLLIDER_SET_MASS);
            ReadInto(ReadFloat, prim, "physics:density", &r.density, &r.setMask, RUSD_PHYSICS_COLLIDER_SET_DENSITY);
            if (_readPhysx)
            {
                ReadInto(ReadFloat, prim, "physxCollision:contactOffset", &r.contactOffset, &r.setMask, RUSD_PHYSICS_COLLIDER_SET_CONTACT_OFFSET);
                ReadInto(ReadFloat, prim, "physxCollision:restOffset", &r.restOffset, &r.setMask, RUSD_PHYSICS_COLLIDER_SET_REST_OFFSET);
            }
        }
        _store->colliders.push_back(r);
    }
}

void Reader::ResolveMaterials()
{
    for (const auto& item : _materials)
    {
        const UsdPhysicsRigidBodyMaterialDesc& d = item.second;
        RUsdPhysicsMaterial r{};
        r.pathIndex = PathString(item.first);
        r.nodeIndex = Node(item.first);
        r.staticFriction = d.staticFriction;
        r.dynamicFriction = d.dynamicFriction;
        r.restitution = d.restitution;
        r.density = d.density;

        const UsdPrim prim = _stage->GetPrimAtPath(item.first);
        if (prim && _readPhysx)
        {
            ReadInto(ReadCombineMode, prim, "physxMaterial:frictionCombineMode", &r.frictionCombineMode, &r.setMask, RUSD_PHYSICS_MATERIAL_SET_FRICTION_COMBINE);
            ReadInto(ReadCombineMode, prim, "physxMaterial:restitutionCombineMode", &r.restitutionCombineMode, &r.setMask, RUSD_PHYSICS_MATERIAL_SET_RESTITUTION_COMBINE);
        }
        _store->materials.push_back(r);
    }
}

// The DOFs a joint moves along, for attributes PhysX authors once per joint or per axis.
std::vector<std::pair<int, std::string>> JointAxes(const JointEntry& entry)
{
    std::vector<std::pair<int, std::string>> axes;
    const RUsdPhysicsJoint& r = entry.record;
    if (r.type == RUSD_PHYSICS_JOINT_REVOLUTE)
    {
        axes.emplace_back(entry.primaryDof, "angular");
    }
    else if (r.type == RUSD_PHYSICS_JOINT_PRISMATIC)
    {
        axes.emplace_back(entry.primaryDof, "linear");
    }
    else if (r.type == RUSD_PHYSICS_JOINT_D6)
    {
        for (int dof = RUSD_PHYSICS_DOF_TRANS_X; dof <= RUSD_PHYSICS_DOF_ROT_Z; ++dof)
        {
            const RUsdPhysicsJointDof& d = r.dofs[dof];
            if (!(d.limitEnabled && d.lower > d.upper))
            {
                axes.emplace_back(dof, DofInstanceName(dof));
            }
        }
    }
    return axes;
}

void Reader::ReadJointPhysx(const UsdPrim& prim, JointEntry* entry)
{
    const std::string mimic = FindAppliedSchema(prim, "PhysxMimicJointAPI");
    if (!mimic.empty())
    {
        Unsupported(entry->path, RUSD_PHYSICS_UNSUPPORTED_MIMIC_JOINT, mimic);
    }
    const std::string tendon = FindAppliedSchema(prim, "PhysxTendon");
    if (!tendon.empty())
    {
        Unsupported(entry->path, RUSD_PHYSICS_UNSUPPORTED_TENDON, tendon);
    }

    // PhysxJointAPI for the whole joint, then PhysxJointAxisAPI:<axis> over it.
    float armature = 0.0f, maxVelocity = 0.0f, friction = 0.0f;
    const bool hasArmature = ReadFloat(prim, "physxJoint:armature", &armature);
    const bool hasMaxVelocity = ReadFloat(prim, "physxJoint:maxJointVelocity", &maxVelocity);
    const bool hasFriction = ReadFloat(prim, "physxJoint:jointFriction", &friction);

    for (const auto& axis : JointAxes(*entry))
    {
        RUsdPhysicsJointDof& dof = entry->record.dofs[axis.first];
        if (hasArmature) { dof.armature = armature; dof.setMask |= RUSD_PHYSICS_DOF_SET_ARMATURE; }
        if (hasMaxVelocity) { dof.maxJointVelocity = maxVelocity; dof.setMask |= RUSD_PHYSICS_DOF_SET_MAX_VELOCITY; }
        if (hasFriction) { dof.jointFriction = friction; dof.setMask |= RUSD_PHYSICS_DOF_SET_FRICTION; }

        const std::string name = axis.second;
        const std::string jointAxis = "physxJointAxis:" + name + ":";
        const std::string state = "state:" + name + ":physics:";
        ReadInto(ReadFloat, prim, (jointAxis + "armature").c_str(), &dof.armature, &dof.setMask, RUSD_PHYSICS_DOF_SET_ARMATURE);
        ReadInto(ReadFloat, prim, (jointAxis + "maxJointVelocity").c_str(), &dof.maxJointVelocity, &dof.setMask, RUSD_PHYSICS_DOF_SET_MAX_VELOCITY);
        ReadInto(ReadFloat, prim, (jointAxis + "jointFriction").c_str(), &dof.jointFriction, &dof.setMask, RUSD_PHYSICS_DOF_SET_FRICTION);
        ReadInto(ReadFloat, prim, (state + "position").c_str(), &dof.statePosition, &dof.setMask, RUSD_PHYSICS_DOF_SET_STATE_POSITION);
        ReadInto(ReadFloat, prim, (state + "velocity").c_str(), &dof.stateVelocity, &dof.setMask, RUSD_PHYSICS_DOF_SET_STATE_VELOCITY);
    }
}

void Reader::ResolveJoints()
{
    for (JointEntry& entry : _joints)
    {
        RUsdPhysicsJoint& r = entry.record;
        const UsdPhysicsJointDesc& d = entry.desc;
        r.pathIndex = PathString(entry.path);
        r.nodeIndex = Node(entry.path);
        const auto body0 = _bodyIndex.find(d.body0);
        const auto body1 = _bodyIndex.find(d.body1);
        r.body0Index = body0 != _bodyIndex.end() ? body0->second : -1;
        r.body1Index = body1 != _bodyIndex.end() ? body1->second : -1;
        r.articulationIndex = -1;
        r.localPosition0 = ToVec3(d.localPose0Position);
        r.localRotation0 = ToQuat(d.localPose0Orientation);
        r.localPosition1 = ToVec3(d.localPose1Position);
        r.localRotation1 = ToQuat(d.localPose1Orientation);
        r.enabled = d.jointEnabled ? 1 : 0;
        r.collisionEnabled = d.collisionEnabled ? 1 : 0;
        r.excludeFromArticulation = d.excludeFromArticulation ? 1 : 0;
        r.breakForce = d.breakForce;
        r.breakTorque = d.breakTorque;

        const UsdPrim prim = _stage->GetPrimAtPath(entry.path);
        if (prim && _readPhysx)
        {
            ReadJointPhysx(prim, &entry);
        }
        _store->joints.push_back(r);
    }
}

void Reader::ResolveArticulations()
{
    for (const auto& item : _articulations)
    {
        const UsdPhysicsArticulationDesc& d = item.second;
        const int index = static_cast<int>(_store->articulations.size());
        RUsdPhysicsArticulation r{};
        r.pathIndex = PathString(item.first);
        r.nodeIndex = Node(item.first);
        r.rootBodyIndex = -1;
        r.bodies = IndexList(d.articulatedBodies, _bodyIndex);
        r.filtered = PathList(d.filteredCollisions);

        // The parser lists every joint between the articulation's bodies, a joint authored with
        // physics:excludeFromArticulation included; that one stays a free joint.
        r.joints.offset = static_cast<int>(_store->indices.size());
        for (const SdfPath& path : d.articulatedJoints)
        {
            const auto joint = _jointIndex.find(path);
            if (joint != _jointIndex.end() && !_store->joints[joint->second].excludeFromArticulation)
            {
                _store->indices.push_back(joint->second);
                ++r.joints.count;
            }
        }

        for (int i = 0; i < r.bodies.count; ++i)
        {
            _store->bodies[_store->indices[r.bodies.offset + i]].articulationIndex = index;
        }
        for (int i = 0; i < r.joints.count; ++i)
        {
            RUsdPhysicsJoint& joint = _store->joints[_store->indices[r.joints.offset + i]];
            joint.articulationIndex = index;
            if (joint.body0Index < 0 || joint.body1Index < 0)
            {
                r.fixedBase = 1;
            }
        }

        for (const SdfPath& root : d.rootPrims)
        {
            const auto body = _bodyIndex.find(root);
            if (body != _bodyIndex.end())
            {
                r.rootBodyIndex = body->second;
                break;
            }
            const auto joint = _jointIndex.find(root);
            if (joint != _jointIndex.end())
            {
                const RUsdPhysicsJoint& j = _store->joints[joint->second];
                r.rootBodyIndex = j.body0Index >= 0 ? j.body0Index : j.body1Index;
                if (r.rootBodyIndex >= 0)
                {
                    break;
                }
            }
        }

        const UsdPrim prim = _stage->GetPrimAtPath(item.first);
        if (prim && _readPhysx)
        {
            ReadInto(ReadInt, prim, "physxArticulation:enabledSelfCollisions", &r.enabledSelfCollisions, &r.setMask, RUSD_PHYSICS_ARTICULATION_SET_SELF_COLLISIONS);
            ReadInto(ReadInt, prim, "physxArticulation:solverPositionIterationCount", &r.solverPositionIterations, &r.setMask, RUSD_PHYSICS_ARTICULATION_SET_POSITION_ITERATIONS);
            ReadInto(ReadInt, prim, "physxArticulation:solverVelocityIterationCount", &r.solverVelocityIterations, &r.setMask, RUSD_PHYSICS_ARTICULATION_SET_VELOCITY_ITERATIONS);
            ReadInto(ReadFloat, prim, "physxArticulation:sleepThreshold", &r.sleepThreshold, &r.setMask, RUSD_PHYSICS_ARTICULATION_SET_SLEEP_THRESHOLD);
            ReadInto(ReadFloat, prim, "physxArticulation:stabilizationThreshold", &r.stabilizationThreshold, &r.setMask, RUSD_PHYSICS_ARTICULATION_SET_STABILIZATION);
        }
        _store->articulations.push_back(r);
    }
}

void Reader::ResolveGroups()
{
    for (const auto& item : _groups)
    {
        const UsdPhysicsCollisionGroupDesc& d = item.second;
        RUsdPhysicsCollisionGroup r{};
        r.pathIndex = PathString(item.first);
        r.nodeIndex = Node(item.first);
        r.invertFilteredGroups = d.invertFilteredGroups ? 1 : 0;
        r.filteredGroups = IndexList(d.filteredGroups, _groupIndex);
        r.mergeGroupNameIndex = d.mergeGroupName.empty() ? -1 : String(d.mergeGroupName);
        _store->groups.push_back(r);
    }
}

void Reader::Resolve()
{
    for (size_t i = 0; i < _bodies.size(); ++i) _bodyIndex.emplace(_bodies[i].first, static_cast<int>(i));
    for (size_t i = 0; i < _shapes.size(); ++i) _shapeIndex.emplace(_shapes[i].path, static_cast<int>(i));
    for (size_t i = 0; i < _materials.size(); ++i) _materialIndex.emplace(_materials[i].first, static_cast<int>(i));
    for (size_t i = 0; i < _joints.size(); ++i) _jointIndex.emplace(_joints[i].path, static_cast<int>(i));
    for (size_t i = 0; i < _groups.size(); ++i) _groupIndex.emplace(_groups[i].first, static_cast<int>(i));

    for (const auto& item : _scenes)
    {
        const UsdPhysicsSceneDesc& d = item.second;
        RUsdPhysicsScene r{};
        r.pathIndex = PathString(item.first);
        r.nodeIndex = Node(item.first);
        r.gravityDirection = ToVec3(d.gravityDirection);
        r.gravityMagnitude = d.gravityMagnitude;

        const UsdPrim prim = _stage->GetPrimAtPath(item.first);
        if (prim && _readPhysx)
        {
            ReadInto(ReadFloat, prim, "physxScene:timeStepsPerSecond", &r.timeStepsPerSecond, &r.setMask, RUSD_PHYSICS_SCENE_SET_TIME_STEPS);
            ReadInto(ReadFloat, prim, "physxScene:bounceThreshold", &r.bounceThreshold, &r.setMask, RUSD_PHYSICS_SCENE_SET_BOUNCE_THRESHOLD);
            ReadInto(ReadInt, prim, "physxScene:minPositionIterationCount", &r.solverPositionIterations, &r.setMask, RUSD_PHYSICS_SCENE_SET_POSITION_ITERATIONS);
            ReadInto(ReadInt, prim, "physxScene:minVelocityIterationCount", &r.solverVelocityIterations, &r.setMask, RUSD_PHYSICS_SCENE_SET_VELOCITY_ITERATIONS);
            ReadInto(ReadInt, prim, "physxScene:enableCCD", &r.enableCCD, &r.setMask, RUSD_PHYSICS_SCENE_SET_CCD);
        }
        _store->scenes.push_back(r);
    }

    ResolveMaterials();
    ResolveGroups();
    ResolveBodies();
    ResolveColliders();
    ResolveJoints();
    ResolveArticulations();
}

void Reader::Finish()
{
    // Every count is an int in the API; a table that outgrew one cannot be handed over.
    const size_t largest = std::max({_store->indices.size(), _store->floats.size(), _store->strings.size()});
    if (largest > static_cast<size_t>(std::numeric_limits<int>::max()))
    {
        throw std::runtime_error("the result is too large to return.");
    }

    RUsdPhysicsData& data = _store->data;
    data.structSize = static_cast<int>(sizeof(RUsdPhysicsData));
    const TfToken upAxis = UsdGeomGetStageUpAxis(_stage);
    data.upAxis = upAxis == UsdGeomTokens->z ? RUSD_PHYSICS_AXIS_Z : RUSD_PHYSICS_AXIS_Y;
    data.metersPerUnit = UsdGeomGetStageMetersPerUnit(_stage);
    data.kilogramsPerUnit = UsdPhysicsGetStageKilogramsPerUnit(_stage);

    data.scenes = _store->scenes.data();
    data.bodies = _store->bodies.data();
    data.colliders = _store->colliders.data();
    data.materials = _store->materials.data();
    data.joints = _store->joints.data();
    data.articulations = _store->articulations.data();
    data.groups = _store->groups.data();
    data.unsupported = _store->unsupported.data();
    data.indices = _store->indices.data();
    data.floats = _store->floats.data();
    data.strings = _store->strings.data();
    data.stringOffsets = _store->stringOffsets.data();

    data.sceneCount = static_cast<int>(_store->scenes.size());
    data.bodyCount = static_cast<int>(_store->bodies.size());
    data.colliderCount = static_cast<int>(_store->colliders.size());
    data.materialCount = static_cast<int>(_store->materials.size());
    data.jointCount = static_cast<int>(_store->joints.size());
    data.articulationCount = static_cast<int>(_store->articulations.size());
    data.groupCount = static_cast<int>(_store->groups.size());
    data.unsupportedCount = static_cast<int>(_store->unsupported.size());
    data.indexCount = static_cast<int>(_store->indices.size());
    data.floatCount = static_cast<int>(_store->floats.size());
    data.stringBytes = static_cast<int>(_store->strings.size());
    data.stringCount = static_cast<int>(_store->stringOffsets.size());
}

std::vector<SdfPath> ParseRoots(const char* rootPaths)
{
    std::vector<SdfPath> roots;
    const std::string text = rootPaths != nullptr ? rootPaths : "";
    for (const std::string& line : TfStringSplit(text, "\n"))
    {
        const std::string trimmed = TfStringTrim(line);
        if (trimmed.empty())
        {
            continue;
        }

        const SdfPath path(trimmed);
        if (path != SdfPath::AbsoluteRootPath() && !(path.IsAbsolutePath() && path.IsPrimPath()))
        {
            throw std::runtime_error("'" + trimmed +
                                     "' is not an absolute prim path.");
        }
        roots.push_back(path);
    }

    if (roots.empty())
    {
        roots.push_back(SdfPath::AbsoluteRootPath());
    }
    return roots;
}

void Reader::Run()
{
    const std::vector<SdfPath> roots = ParseRoots(_options.rootPaths);
    for (const SdfPath& root : roots)
    {
        if (!_stage->GetPrimAtPath(root))
        {
            throw std::runtime_error("there is no prim at " + root.GetString() + ".");
        }
    }

    // The parser calls this once per object kind, on the calling thread, after its parallel parse.
    const UsdPhysicsReportFn report = [this](UsdPhysicsObjectType type, TfSpan<const SdfPath> paths,
                                             TfSpan<const UsdPhysicsObjectDesc> descs, const VtValue&) {
        Report(type, paths, descs);
    };

    if (!UsdPhysicsLoadStageFromPrimRange(_stage, roots, report, VtValue()))
    {
        throw std::runtime_error("OpenUSD's physics parser failed.");
    }

    Resolve();
    Finish();
}

} // namespace

StorePtr Read(const UsdStageRefPtr& stage, const RUsdPhysicsReadOptions& options,
              const ImportLookups& lookups)
{
    StorePtr store(new Store());
    Reader reader(stage, options, lookups, store.get());
    reader.Run();
    return store;
}

} // namespace rusd_physics
