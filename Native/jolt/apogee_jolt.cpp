// Our C API over Jolt Physics: see apogee_jolt.h.

#include "apogee_jolt.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <deque>
#include <mutex>
#include <shared_mutex>
#include <unordered_set>
#include <vector>

#include <Jolt/Jolt.h>

#include <Jolt/Core/Factory.h>
#include <Jolt/Core/JobSystemThreadPool.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Math/Quat.h>
#include <Jolt/Physics/Body/BodyActivationListener.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <Jolt/Physics/Body/BodyLock.h>
#include <Jolt/Physics/Character/CharacterVirtual.h>
#include <Jolt/Physics/Collision/BroadPhase/BroadPhaseLayer.h>
#include <Jolt/Physics/Collision/CastResult.h>
#include <Jolt/Physics/Collision/CollideShape.h>
#include <Jolt/Physics/Collision/CollisionCollectorImpl.h>
#include <Jolt/Physics/Collision/ContactListener.h>
#include <Jolt/Physics/Collision/ObjectLayer.h>
#include <Jolt/Physics/Collision/RayCast.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Collision/Shape/CapsuleShape.h>
#include <Jolt/Physics/Collision/Shape/ConvexHullShape.h>
#include <Jolt/Physics/Collision/Shape/CylinderShape.h>
#include <Jolt/Physics/Collision/Shape/HeightFieldShape.h>
#include <Jolt/Physics/Collision/Shape/MeshShape.h>
#include <Jolt/Physics/Collision/Shape/MutableCompoundShape.h>
#include <Jolt/Physics/Collision/Shape/OffsetCenterOfMassShape.h>
#include <Jolt/Physics/Collision/Shape/RotatedTranslatedShape.h>
#include <Jolt/Physics/Collision/Shape/ScaledShape.h>
#include <Jolt/Physics/Collision/Shape/SphereShape.h>
#include <Jolt/Physics/Collision/Shape/StaticCompoundShape.h>
#include <Jolt/Physics/Collision/ShapeCast.h>
#include <Jolt/Physics/Constraints/SixDOFConstraint.h>
#include <Jolt/Physics/PhysicsSettings.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/Vehicle/TrackedVehicleController.h>
#include <Jolt/Physics/Vehicle/VehicleCollisionTester.h>
#include <Jolt/Physics/Vehicle/VehicleConstraint.h>
#include <Jolt/Physics/Vehicle/WheeledVehicleController.h>
#include <Jolt/RegisterTypes.h>
#include <Jolt/Renderer/DebugRendererSimple.h>

using namespace JPH;

namespace
{
    // ---- Conversions -------------------------------------------------------------------------------------------------------------

    inline Vec3 ToJolt(AjVec3 v) { return Vec3(v.x, v.y, v.z); }
    inline Quat ToJolt(AjQuat q) { return Quat(q.x, q.y, q.z, q.w); }
    inline RVec3 ToJolt(AjRVec3 v) { return RVec3(v.x, v.y, v.z); }
    inline AjVec3 FromJolt(Vec3Arg v) { return AjVec3{ v.GetX(), v.GetY(), v.GetZ() }; }
    inline AjQuat FromJolt(QuatArg q) { return AjQuat{ q.GetX(), q.GetY(), q.GetZ(), q.GetW() }; }
    inline AjRVec3 FromJolt(RVec3Arg v) { return AjRVec3{ double(v.GetX()), double(v.GetY()), double(v.GetZ()) }; }

    inline Shape* ToJolt(AjShape* shape) { return reinterpret_cast<Shape*>(shape); }
    inline const Shape* ToJolt(const AjShape* shape) { return reinterpret_cast<const Shape*>(shape); }

    // A new shape comes with one reference, which the caller of the C API owns.
    AjShape* Own(const Shape* shape)
    {
        if (shape == nullptr)
        {
            return nullptr;
        }

        shape->AddRef();
        return reinterpret_cast<AjShape*>(const_cast<Shape*>(shape));
    }

    AjShape* Own(const ShapeSettings::ShapeResult& result) { return result.HasError() ? nullptr : Own(result.Get().GetPtr()); }

    // ---- Collision layers --------------------------------------------------------------------------------------------------------

    // An object layer is 32 bits: the low 16 are the body's group (its layers, 14 bits, with 0x4000 for a static body and 0x8000 for a sensor, which only choose the broad
    // phase tree), the high 16 its mask. Two bodies collide when each is in a layer the other's mask holds.
    constexpr uint32 cStaticBit = 0x4000u;
    constexpr uint32 cSensorBit = 0x8000u;
    constexpr uint32 cGroupMask = 0xFFFFu;

    ObjectLayer MakeObjectLayer(uint32 layers, uint32 mask, AjMotionType motion, bool sensor)
    {
        uint32 group = layers & AJ_LAYER_BITS;
        if (sensor)
        {
            group |= cSensorBit;
        }
        else if (motion == AJ_MOTION_STATIC)
        {
            group |= cStaticBit;
        }

        return ObjectLayer(group | ((mask & AJ_LAYER_BITS) << 16));
    }

    class BroadPhaseLayers final : public BroadPhaseLayerInterface
    {
    public:
        uint GetNumBroadPhaseLayers() const override { return 3; } // 0 static, 1 sensor, 2 moving

        BroadPhaseLayer GetBroadPhaseLayer(ObjectLayer layer) const override
        {
            uint32 group = uint32(layer) & cGroupMask;
            if (group & cStaticBit)
            {
                return BroadPhaseLayer(0);
            }

            return BroadPhaseLayer((group & cSensorBit) ? 1 : 2);
        }

#if defined(JPH_EXTERNAL_PROFILE) || defined(JPH_PROFILE_ENABLED)
        const char* GetBroadPhaseLayerName(BroadPhaseLayer layer) const override
        {
            static const char* names[] = { "static", "sensor", "moving" };
            return names[(BroadPhaseLayer::Type)layer];
        }
#endif
    };

    class PairFilter final : public ObjectLayerPairFilter
    {
    public:
        bool ShouldCollide(ObjectLayer a, ObjectLayer b) const override
        {
            uint32 groupA = uint32(a) & AJ_LAYER_BITS;
            uint32 groupB = uint32(b) & AJ_LAYER_BITS;
            uint32 maskA = (uint32(a) >> 16) & AJ_LAYER_BITS;
            uint32 maskB = (uint32(b) >> 16) & AJ_LAYER_BITS;
            return (groupA & maskB) != 0 && (groupB & maskA) != 0;
        }
    };

    class ObjectVsBroadPhase final : public ObjectVsBroadPhaseLayerFilter
    {
    public:
        bool ShouldCollide(ObjectLayer layer, BroadPhaseLayer broadPhase) const override
        {
            // Static bodies do not collide with each other; everything else may meet anything.
            return !((uint32(layer) & cStaticBit) != 0 && (BroadPhaseLayer::Type)broadPhase == 0);
        }
    };

    // A query's filters: the layers it asks for (never a sensor), and one body it passes through.
    class QueryLayers final : public ObjectLayerFilter
    {
    public:
        explicit QueryLayers(uint32 mask) : mMask(mask & AJ_LAYER_BITS) {}

        bool ShouldCollide(ObjectLayer layer) const override
        {
            uint32 group = uint32(layer) & cGroupMask;
            return (group & cSensorBit) == 0 && (group & mMask) != 0;
        }

    private:
        uint32 mMask;
    };

    class IgnoreBody final : public BodyFilter
    {
    public:
        explicit IgnoreBody(AjBodyId ignore) : mIgnore(ignore) {}

        bool ShouldCollide(const BodyID& id) const override { return id.GetIndexAndSequenceNumber() != mIgnore; }

    private:
        AjBodyId mIgnore;
    };
}

// ---- The world ---------------------------------------------------------------------------------------------------------------------

constexpr uint64 cEventsBit = 0x8000000000000000ULL; // set in a body's user data when it reports its contacts and activations

struct AjWorld final : public ContactListener, public BodyActivationListener
{
    BroadPhaseLayers broadPhaseLayers;
    PairFilter pairFilter;
    ObjectVsBroadPhase objectVsBroadPhase;
    TempAllocatorImplWithMallocFallback temp; // a buffer that is too small costs time, not a crash
    JobSystemThreadPool jobs;
    PhysicsSystem system;
    uint32 eventCapacity;

    std::mutex eventsMutex; // the listeners run on Jolt's worker threads
    std::deque<AjEvent> events;
    std::unordered_set<uint64> reported; // the pairs whose start was reported, so that their end is too
    uint32 dropped = 0;
    std::shared_mutex ignoredMutex;
    std::unordered_set<uint64> ignored;
    std::atomic<uint32> ignoredCount{ 0 };

    AjWorld(const AjWorldDesc& desc)
        : temp(desc.tempBufferBytes),
          jobs(cMaxPhysicsJobs, cMaxPhysicsBarriers, std::max(desc.threads, 1)),
          eventCapacity(desc.eventCapacity)
    {
        system.Init(desc.maxBodies, 0, desc.maxBodyPairs, desc.maxContactConstraints, broadPhaseLayers, objectVsBroadPhase, pairFilter);
        PhysicsSettings settings = system.GetPhysicsSettings();
        settings.mNumVelocitySteps = desc.velocitySteps;
        settings.mNumPositionSteps = desc.positionSteps;
        settings.mPenetrationSlop = 0.02f;
        settings.mSpeculativeContactDistance = 0.02f;
        settings.mBaumgarte = 0.2f;
        system.SetPhysicsSettings(settings);
        system.SetGravity(ToJolt(desc.gravity));
        system.SetContactListener(this);
        system.SetBodyActivationListener(this);
    }

    ~AjWorld() override
    {
        system.SetContactListener(nullptr);
        system.SetBodyActivationListener(nullptr);
    }

    static uint64 PairKey(uint32 a, uint32 b) { return a < b ? (uint64(a) << 32) | b : (uint64(b) << 32) | a; }

    static uint64 PairKey(const SubShapeIDPair& pair)
    {
        // The pair of shapes: both bodies and both sub shapes, in the order Jolt names them.
        uint64 a = (uint64(pair.GetBody1ID().GetIndexAndSequenceNumber()) << 32) | pair.GetSubShapeID1().GetValue();
        uint64 b = (uint64(pair.GetBody2ID().GetIndexAndSequenceNumber()) << 32) | pair.GetSubShapeID2().GetValue();
        return a * 0x9E3779B97F4A7C15ULL ^ (b + 0x7F4A7C15ULL + (a << 6) + (a >> 2));
    }

    void Push(const AjEvent& event) // with eventsMutex held
    {
        if (events.size() >= eventCapacity)
        {
            dropped++;
            return;
        }

        events.push_back(event);
    }

    ValidateResult OnContactValidate(const Body& body1, const Body& body2, RVec3Arg, const CollideShapeResult&) override
    {
        if (ignoredCount.load(std::memory_order_relaxed) != 0)
        {
            std::shared_lock<std::shared_mutex> lock(ignoredMutex);
            if (ignored.count(PairKey(body1.GetID().GetIndexAndSequenceNumber(), body2.GetID().GetIndexAndSequenceNumber())) != 0)
            {
                return ValidateResult::RejectAllContactsForThisBodyPair;
            }
        }

        return ValidateResult::AcceptAllContactsForThisBodyPair;
    }

    void OnContactAdded(const Body& body1, const Body& body2, const ContactManifold& manifold, ContactSettings&) override
    {
        uint64 user1 = body1.GetUserData();
        uint64 user2 = body2.GetUserData();
        bool sensor = body1.IsSensor() || body2.IsSensor();
        if (!sensor && ((user1 | user2) & cEventsBit) == 0)
        {
            return;
        }

        AjEvent event;
        std::memset(&event, 0, sizeof(event));
        event.kind = AJ_EVENT_CONTACT_ADDED;
        event.body1 = body1.GetID().GetIndexAndSequenceNumber();
        event.body2 = body2.GetID().GetIndexAndSequenceNumber();
        event.flags = sensor ? AJ_EVENT_SENSOR : 0;
        event.userData1 = user1 & ~cEventsBit;
        event.userData2 = user2 & ~cEventsBit;
        event.normal = FromJolt(manifold.mWorldSpaceNormal);
        event.depth = manifold.mPenetrationDepth;
        if (manifold.mRelativeContactPointsOn1.size() > 0)
        {
            event.position = FromJolt(manifold.GetWorldSpaceContactPointOn1(0));
        }

        std::lock_guard<std::mutex> lock(eventsMutex);
        reported.insert(PairKey(SubShapeIDPair(body1.GetID(), manifold.mSubShapeID1, body2.GetID(), manifold.mSubShapeID2)));
        Push(event);
    }

    void OnContactRemoved(const SubShapeIDPair& pair) override
    {
        std::lock_guard<std::mutex> lock(eventsMutex);
        if (reported.erase(PairKey(pair)) == 0)
        {
            return;
        }

        AjEvent event;
        std::memset(&event, 0, sizeof(event));
        event.kind = AJ_EVENT_CONTACT_REMOVED;
        event.body1 = pair.GetBody1ID().GetIndexAndSequenceNumber();
        event.body2 = pair.GetBody2ID().GetIndexAndSequenceNumber();
        Push(event);
    }

    void Activation(int32 kind, const BodyID& id, uint64 userData)
    {
        if ((userData & cEventsBit) == 0)
        {
            return;
        }

        AjEvent event;
        std::memset(&event, 0, sizeof(event));
        event.kind = kind;
        event.body1 = id.GetIndexAndSequenceNumber();
        event.userData1 = userData & ~cEventsBit;
        std::lock_guard<std::mutex> lock(eventsMutex);
        Push(event);
    }

    void OnBodyActivated(const BodyID& id, uint64 userData) override { Activation(AJ_EVENT_BODY_ACTIVATED, id, userData); }

    void OnBodyDeactivated(const BodyID& id, uint64 userData) override { Activation(AJ_EVENT_BODY_DEACTIVATED, id, userData); }
};

namespace
{
    BodyInterface& Bodies(AjWorld* world) { return world->system.GetBodyInterface(); }

    const BodyInterface& Bodies(const AjWorld* world) { return world->system.GetBodyInterface(); }

    inline BodyID Id(AjBodyId id) { return BodyID(id); }
}

#ifdef JPH_DEBUG_RENDERER
namespace
{
    // The collector Jolt draws into. DebugRendererSimple turns solid geometry into DrawTriangle calls and wireframe geometry into DrawLine calls; we keep both lists as they come.
    class DebugCollector final : public DebugRendererSimple
    {
    public:
        std::vector<AjDebugLine> lines;
        std::vector<AjDebugTriangle> triangles;

        void DrawLine(RVec3Arg from, RVec3Arg to, ColorArg color) override
        {
            lines.push_back(AjDebugLine{ { from.GetX(), from.GetY(), from.GetZ() }, { to.GetX(), to.GetY(), to.GetZ() }, color.GetUInt32(), 0 });
        }

        void DrawTriangle(RVec3Arg a, RVec3Arg b, RVec3Arg c, ColorArg color, ECastShadow) override
        {
            triangles.push_back(AjDebugTriangle{ { a.GetX(), a.GetY(), a.GetZ() }, { b.GetX(), b.GetY(), b.GetZ() }, { c.GetX(), c.GetY(), c.GetZ() }, color.GetUInt32(), 0 });
        }

        void DrawText3D(RVec3Arg, const std::string_view&, ColorArg, float) override
        {
            // Text is not drawn: the collector has only lines and triangles.
        }
    };

    DebugCollector* g_collector = nullptr;

    DebugCollector& Collector()
    {
        if (g_collector == nullptr)
        {
            g_collector = new DebugCollector();
        }

        return *g_collector;
    }

    void FreeDebugCollector()
    {
        delete g_collector;
        g_collector = nullptr;
    }
}
#else
namespace
{
    void FreeDebugCollector() {}
}
#endif

extern "C"
{
    // ---- The library -----------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API bool aj_init(void)
    {
        RegisterDefaultAllocator();
        if (Factory::sInstance == nullptr)
        {
            Factory::sInstance = new Factory();
        }

        RegisterTypes();
        return true;
    }

    APOGEE_JOLT_API void aj_shutdown(void)
    {
        FreeDebugCollector();
        UnregisterTypes();
        delete Factory::sInstance;
        Factory::sInstance = nullptr;
    }

    // ---- The world -------------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API AjWorld* aj_world_create(const AjWorldDesc* desc) { return desc == nullptr ? nullptr : new AjWorld(*desc); }

    APOGEE_JOLT_API void aj_world_destroy(AjWorld* world) { delete world; }

    APOGEE_JOLT_API uint32_t aj_world_update(AjWorld* world, float deltaTime)
    {
        return uint32_t(world->system.Update(deltaTime, 1, &world->temp, &world->jobs));
    }

    APOGEE_JOLT_API void aj_world_set_gravity(AjWorld* world, AjVec3 gravity) { world->system.SetGravity(ToJolt(gravity)); }

    APOGEE_JOLT_API AjVec3 aj_world_get_gravity(const AjWorld* world) { return FromJolt(world->system.GetGravity()); }

    APOGEE_JOLT_API uint32_t aj_world_get_active_bodies(const AjWorld* world, AjBodyId* ids, uint32_t capacity)
    {
        BodyIDVector active;
        world->system.GetActiveBodies(EBodyType::RigidBody, active);
        for (uint32_t i = 0; i < capacity && i < active.size(); i++)
        {
            ids[i] = active[i].GetIndexAndSequenceNumber();
        }

        return uint32_t(active.size());
    }

    // ---- Events ----------------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API uint32_t aj_events_take(AjWorld* world, AjEvent* out, uint32_t max)
    {
        std::lock_guard<std::mutex> lock(world->eventsMutex);
        uint32_t count = 0;
        while (count < max && !world->events.empty())
        {
            out[count++] = world->events.front();
            world->events.pop_front();
        }

        return count;
    }

    APOGEE_JOLT_API uint32_t aj_events_dropped(AjWorld* world)
    {
        std::lock_guard<std::mutex> lock(world->eventsMutex);
        uint32_t dropped = world->dropped;
        world->dropped = 0;
        return dropped;
    }

    APOGEE_JOLT_API void aj_ignore_pair(AjWorld* world, AjBodyId a, AjBodyId b)
    {
        std::unique_lock<std::shared_mutex> lock(world->ignoredMutex);
        world->ignored.insert(AjWorld::PairKey(a, b));
        world->ignoredCount.store(uint32_t(world->ignored.size()), std::memory_order_relaxed);
    }

    APOGEE_JOLT_API void aj_allow_pair(AjWorld* world, AjBodyId a, AjBodyId b)
    {
        std::unique_lock<std::shared_mutex> lock(world->ignoredMutex);
        world->ignored.erase(AjWorld::PairKey(a, b));
        world->ignoredCount.store(uint32_t(world->ignored.size()), std::memory_order_relaxed);
    }

    // ---- Shapes ----------------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API AjShape* aj_shape_box(AjVec3 halfExtents, float convexRadius)
    {
        float radius = std::min(std::max(convexRadius, 0.0f), std::min(halfExtents.x, std::min(halfExtents.y, halfExtents.z)));
        return Own(new BoxShape(ToJolt(halfExtents), radius));
    }

    APOGEE_JOLT_API AjShape* aj_shape_sphere(float radius) { return Own(new SphereShape(radius)); }

    APOGEE_JOLT_API AjShape* aj_shape_capsule(float halfHeight, float radius) { return Own(new CapsuleShape(halfHeight, radius)); }

    APOGEE_JOLT_API AjShape* aj_shape_cylinder(float halfHeight, float radius) { return Own(new CylinderShape(halfHeight, radius)); }

    APOGEE_JOLT_API AjShape* aj_shape_convex_hull(const AjVec3* points, uint32_t count)
    {
        Array<Vec3> hull;
        hull.reserve(count);
        for (uint32_t i = 0; i < count; i++)
        {
            hull.push_back(ToJolt(points[i]));
        }

        ConvexHullShapeSettings settings(hull);
        return Own(settings.Create());
    }

    APOGEE_JOLT_API AjShape* aj_shape_mesh(const AjVec3* vertices, uint32_t vertexCount, const uint32_t* indices, uint32_t triangleCount)
    {
        VertexList vertexList;
        vertexList.reserve(vertexCount);
        for (uint32_t i = 0; i < vertexCount; i++)
        {
            vertexList.push_back(Float3(vertices[i].x, vertices[i].y, vertices[i].z));
        }

        IndexedTriangleList triangles;
        triangles.reserve(triangleCount);
        for (uint32_t i = 0; i < triangleCount; i++)
        {
            triangles.push_back(IndexedTriangle(indices[i * 3], indices[(i * 3) + 1], indices[(i * 3) + 2]));
        }

        MeshShapeSettings settings(std::move(vertexList), std::move(triangles));
        return Own(settings.Create());
    }

    APOGEE_JOLT_API AjShape* aj_shape_height_field(const float* heights, uint32_t size, AjVec3 offset, AjVec3 scale)
    {
        HeightFieldShapeSettings settings(heights, ToJolt(offset), ToJolt(scale), size);
        return Own(settings.Create());
    }

    APOGEE_JOLT_API AjShape* aj_shape_static_compound(AjShape* const* children, const AjVec3* positions, const AjQuat* rotations, uint32_t count)
    {
        StaticCompoundShapeSettings settings;
        for (uint32_t i = 0; i < count; i++)
        {
            settings.AddShape(ToJolt(positions[i]), rotations != nullptr ? ToJolt(rotations[i]) : Quat::sIdentity(), ToJolt(children[i]));
        }

        return Own(settings.Create());
    }

    APOGEE_JOLT_API AjShape* aj_shape_offset(AjShape* shape, AjVec3 position, AjQuat rotation)
    {
        return Own(new RotatedTranslatedShape(ToJolt(position), ToJolt(rotation), ToJolt(shape)));
    }

    APOGEE_JOLT_API AjShape* aj_shape_scaled(AjShape* shape, AjVec3 scale) { return Own(new ScaledShape(ToJolt(shape), ToJolt(scale))); }

    APOGEE_JOLT_API AjShape* aj_shape_offset_center_of_mass(AjShape* shape, AjVec3 offset)
    {
        return Own(new OffsetCenterOfMassShape(ToJolt(shape), ToJolt(offset)));
    }

    APOGEE_JOLT_API AjShape* aj_mutable_compound(AjShape* first)
    {
        MutableCompoundShapeSettings settings;
        settings.AddShape(Vec3::sZero(), Quat::sIdentity(), ToJolt(first));
        return Own(settings.Create());
    }

    APOGEE_JOLT_API uint32_t aj_mutable_compound_add(AjShape* compound, AjShape* child, AjVec3 position, AjQuat rotation)
    {
        return static_cast<MutableCompoundShape*>(ToJolt(compound))->AddShape(ToJolt(position), ToJolt(rotation), ToJolt(child));
    }

    APOGEE_JOLT_API void aj_mutable_compound_modify(AjShape* compound, uint32_t index, AjVec3 position, AjQuat rotation)
    {
        static_cast<MutableCompoundShape*>(ToJolt(compound))->ModifyShape(index, ToJolt(position), ToJolt(rotation));
    }

    APOGEE_JOLT_API void aj_mutable_compound_remove(AjShape* compound, uint32_t index)
    {
        static_cast<MutableCompoundShape*>(ToJolt(compound))->RemoveShape(index);
    }

    APOGEE_JOLT_API void aj_mutable_compound_adjust_center_of_mass(AjShape* compound)
    {
        static_cast<MutableCompoundShape*>(ToJolt(compound))->AdjustCenterOfMass();
    }

    APOGEE_JOLT_API void aj_shape_release(AjShape* shape)
    {
        if (shape != nullptr)
        {
            ToJolt(shape)->Release();
        }
    }

    APOGEE_JOLT_API float aj_shape_volume(const AjShape* shape) { return ToJolt(shape)->GetVolume(); }

    APOGEE_JOLT_API AjVec3 aj_shape_center_of_mass(const AjShape* shape) { return FromJolt(ToJolt(shape)->GetCenterOfMass()); }

    APOGEE_JOLT_API bool aj_shape_is_valid_scale(const AjShape* shape, AjVec3 scale) { return ToJolt(shape)->IsValidScale(ToJolt(scale)); }

    // ---- Bodies ----------------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API AjBodyId aj_body_create(AjWorld* world, const AjBodyDesc* desc)
    {
        EMotionType motion = desc->motion == AJ_MOTION_STATIC ? EMotionType::Static : desc->motion == AJ_MOTION_KINEMATIC ? EMotionType::Kinematic : EMotionType::Dynamic;
        BodyCreationSettings settings(ToJolt(desc->shape), ToJolt(desc->position), ToJolt(desc->rotation), motion, MakeObjectLayer(desc->layers, desc->mask, desc->motion, desc->sensor));
        settings.mUserData = (desc->userData & ~cEventsBit) | (desc->events ? cEventsBit : 0);
        settings.mFriction = desc->friction;
        settings.mRestitution = desc->restitution;
        settings.mLinearDamping = desc->linearDamping;
        settings.mAngularDamping = desc->angularDamping;
        settings.mGravityFactor = desc->gravityFactor;
        settings.mIsSensor = desc->sensor;
        settings.mAllowSleeping = desc->allowSleeping;
        settings.mMotionQuality = desc->bullet ? EMotionQuality::LinearCast : EMotionQuality::Discrete;
        settings.mLinearVelocity = ToJolt(desc->linearVelocity);
        settings.mAngularVelocity = ToJolt(desc->angularVelocity);
        if (desc->motion == AJ_MOTION_DYNAMIC && desc->mass > 0)
        {
            // Otherwise Jolt takes the mass from the shape's volume and its density (1000 kg/m^3 for ours).
            settings.mOverrideMassProperties = EOverrideMassProperties::CalculateInertia;
            settings.mMassPropertiesOverride.mMass = std::max(desc->mass, 0.001f);
        }

        BodyID id = Bodies(world).CreateAndAddBody(settings, desc->motion == AJ_MOTION_STATIC ? EActivation::DontActivate : EActivation::Activate);
        return id.IsInvalid() ? AJ_INVALID_BODY : id.GetIndexAndSequenceNumber();
    }

    APOGEE_JOLT_API void aj_body_destroy(AjWorld* world, AjBodyId body)
    {
        BodyInterface& bodies = Bodies(world);
        bodies.RemoveBody(Id(body));
        bodies.DestroyBody(Id(body));
    }

    APOGEE_JOLT_API void aj_body_get_pose(const AjWorld* world, AjBodyId body, AjRVec3* position, AjQuat* rotation)
    {
        RVec3 p;
        Quat r;
        Bodies(world).GetPositionAndRotation(Id(body), p, r);
        *position = FromJolt(p);
        *rotation = FromJolt(r);
    }

    APOGEE_JOLT_API void aj_body_set_pose(AjWorld* world, AjBodyId body, AjRVec3 position, AjQuat rotation)
    {
        BodyInterface& bodies = Bodies(world);
        bodies.SetPositionAndRotation(Id(body), ToJolt(position), ToJolt(rotation), bodies.GetMotionType(Id(body)) == EMotionType::Static ? EActivation::DontActivate : EActivation::Activate);
    }

    APOGEE_JOLT_API void aj_body_move_kinematic(AjWorld* world, AjBodyId body, AjRVec3 position, AjQuat rotation, float deltaTime)
    {
        Bodies(world).MoveKinematic(Id(body), ToJolt(position), ToJolt(rotation), deltaTime);
    }

    APOGEE_JOLT_API AjVec3 aj_body_get_linear_velocity(const AjWorld* world, AjBodyId body) { return FromJolt(Bodies(world).GetLinearVelocity(Id(body))); }

    APOGEE_JOLT_API void aj_body_set_linear_velocity(AjWorld* world, AjBodyId body, AjVec3 velocity) { Bodies(world).SetLinearVelocity(Id(body), ToJolt(velocity)); }

    APOGEE_JOLT_API AjVec3 aj_body_get_angular_velocity(const AjWorld* world, AjBodyId body) { return FromJolt(Bodies(world).GetAngularVelocity(Id(body))); }

    APOGEE_JOLT_API void aj_body_set_angular_velocity(AjWorld* world, AjBodyId body, AjVec3 velocity) { Bodies(world).SetAngularVelocity(Id(body), ToJolt(velocity)); }

    APOGEE_JOLT_API void aj_body_add_force(AjWorld* world, AjBodyId body, AjVec3 force) { Bodies(world).AddForce(Id(body), ToJolt(force)); }

    APOGEE_JOLT_API void aj_body_add_torque(AjWorld* world, AjBodyId body, AjVec3 torque) { Bodies(world).AddTorque(Id(body), ToJolt(torque)); }

    APOGEE_JOLT_API void aj_body_add_impulse(AjWorld* world, AjBodyId body, AjVec3 impulse) { Bodies(world).AddImpulse(Id(body), ToJolt(impulse)); }

    APOGEE_JOLT_API void aj_body_activate(AjWorld* world, AjBodyId body) { Bodies(world).ActivateBody(Id(body)); }

    APOGEE_JOLT_API bool aj_body_is_active(const AjWorld* world, AjBodyId body) { return Bodies(world).IsActive(Id(body)); }

    APOGEE_JOLT_API AjShape* aj_body_get_shape(const AjWorld* world, AjBodyId body)
    {
        return reinterpret_cast<AjShape*>(const_cast<Shape*>(Bodies(world).GetShape(Id(body)).GetPtr()));
    }

    APOGEE_JOLT_API void aj_body_set_shape(AjWorld* world, AjBodyId body, AjShape* shape, bool updateMass)
    {
        Bodies(world).SetShape(Id(body), ToJolt(shape), updateMass, EActivation::Activate);
    }

    APOGEE_JOLT_API void aj_body_notify_shape_changed(AjWorld* world, AjBodyId body, AjVec3 previousCenterOfMass)
    {
        Bodies(world).NotifyShapeChanged(Id(body), ToJolt(previousCenterOfMass), false, EActivation::Activate);
    }

    APOGEE_JOLT_API float aj_body_get_mass(const AjWorld* world, AjBodyId body)
    {
        BodyLockRead lock(world->system.GetBodyLockInterface(), Id(body));
        if (!lock.Succeeded() || !lock.GetBody().IsDynamic())
        {
            return 0;
        }

        float inverse = lock.GetBody().GetMotionProperties()->GetInverseMass();
        return inverse > 0 ? 1.0f / inverse : 0;
    }

    APOGEE_JOLT_API void aj_body_set_mass(AjWorld* world, AjBodyId body, float mass)
    {
        BodyLockWrite lock(world->system.GetBodyLockInterface(), Id(body));
        if (!lock.Succeeded() || !lock.GetBody().IsDynamic())
        {
            return;
        }

        MassProperties properties = lock.GetBody().GetShape()->GetMassProperties();
        properties.ScaleToMass(std::max(mass, 0.001f));
        lock.GetBody().GetMotionProperties()->SetMassProperties(EAllowedDOFs::All, properties);
    }

    // ---- Queries ---------------------------------------------------------------------------------------------------------------

    APOGEE_JOLT_API bool aj_raycast(const AjWorld* world, AjRVec3 origin, AjVec3 direction, uint32_t mask, AjBodyId ignore, AjRayHit* hit)
    {
        QueryLayers layers(mask);
        IgnoreBody filter(ignore);
        RRayCast ray(ToJolt(origin), ToJolt(direction));
        RayCastResult result;
        if (!world->system.GetNarrowPhaseQuery().CastRay(ray, result, {}, layers, filter))
        {
            return false;
        }

        hit->body = result.mBodyID.GetIndexAndSequenceNumber();
        hit->fraction = result.mFraction;
        hit->normal = AjVec3{ 0, 1, 0 };
        BodyLockRead lock(world->system.GetBodyLockInterface(), result.mBodyID);
        if (lock.Succeeded())
        {
            hit->normal = FromJolt(lock.GetBody().GetWorldSpaceSurfaceNormal(result.mSubShapeID2, ray.GetPointOnRay(result.mFraction)));
        }

        return true;
    }

    APOGEE_JOLT_API bool aj_cast_sphere(const AjWorld* world, AjRVec3 origin, AjVec3 direction, float radius, uint32_t mask, AjBodyId ignore, AjCastHit* hit)
    {
        QueryLayers layers(mask);
        IgnoreBody filter(ignore);
        SphereShape sphere(radius);
        sphere.SetEmbedded();
        RShapeCast cast = RShapeCast::sFromWorldTransform(&sphere, Vec3::sReplicate(1.0f), RMat44::sTranslation(ToJolt(origin)), ToJolt(direction));
        ShapeCastSettings settings;
        ClosestHitCollisionCollector<CastShapeCollector> collector;
        world->system.GetNarrowPhaseQuery().CastShape(cast, settings, ToJolt(origin), collector, {}, layers, filter);
        if (!collector.HadHit())
        {
            return false;
        }

        hit->body = collector.mHit.mBodyID2.GetIndexAndSequenceNumber();
        hit->fraction = collector.mHit.mFraction;
        hit->point = FromJolt(ToJolt(origin) + collector.mHit.mContactPointOn2);
        hit->normal = FromJolt(-collector.mHit.mPenetrationAxis.NormalizedOr(Vec3::sAxisY()));
        return true;
    }

    static uint32_t Overlap(const AjWorld* world, const Shape* shape, RMat44Arg transform, RVec3Arg center, uint32_t mask, AjBodyId ignore, AjBodyId* ids, uint32_t capacity)
    {
        QueryLayers layers(mask);
        IgnoreBody filter(ignore);
        CollideShapeSettings settings;
        AllHitCollisionCollector<CollideShapeCollector> collector;
        world->system.GetNarrowPhaseQuery().CollideShape(shape, Vec3::sReplicate(1.0f), transform, settings, center, collector, {}, layers, filter);
        uint32_t count = 0;
        std::vector<uint32_t> seen;
        for (const CollideShapeResult& hit : collector.mHits)
        {
            uint32_t id = hit.mBodyID2.GetIndexAndSequenceNumber();
            if (std::find(seen.begin(), seen.end(), id) == seen.end())
            {
                seen.push_back(id);
                if (count < capacity)
                {
                    ids[count] = id;
                }

                count++;
            }
        }

        return count;
    }

    APOGEE_JOLT_API uint32_t aj_overlap_sphere(const AjWorld* world, AjRVec3 center, float radius, uint32_t mask, AjBodyId ignore, AjBodyId* ids, uint32_t capacity)
    {
        SphereShape sphere(radius);
        sphere.SetEmbedded();
        return Overlap(world, &sphere, RMat44::sTranslation(ToJolt(center)), ToJolt(center), mask, ignore, ids, capacity);
    }

    APOGEE_JOLT_API uint32_t aj_overlap_box(const AjWorld* world, AjRVec3 center, AjQuat rotation, AjVec3 halfExtents, uint32_t mask, AjBodyId ignore, AjBodyId* ids, uint32_t capacity)
    {
        BoxShape box(ToJolt(halfExtents), 0.0f);
        box.SetEmbedded();
        return Overlap(world, &box, RMat44::sRotationTranslation(ToJolt(rotation), ToJolt(center)), ToJolt(center), mask, ignore, ids, capacity);
    }
}

// ---- Character controller --------------------------------------------------------------------------------------------------------

struct AjCharacter
{
    AjWorld* world;
    Ref<CharacterVirtual> character;
    ObjectLayer layer;
};

extern "C"
{
    APOGEE_JOLT_API AjCharacter* aj_character_create(AjWorld* world, const AjCharacterDesc* desc)
    {
        // The feet are the origin: a capsule moved up by half the height.
        float halfCylinder = (desc->height / 2) - desc->radius;
        RefConst<Shape> standing = RotatedTranslatedShapeSettings(Vec3(0, desc->height / 2, 0), Quat::sIdentity(), new CapsuleShape(halfCylinder, desc->radius)).Create().Get();
        CharacterVirtualSettings settings;
        settings.mShape = standing;
        settings.mUp = Vec3::sAxisY();
        settings.mMaxSlopeAngle = desc->maxSlopeRadians;
        settings.mMass = desc->mass;
        settings.mMaxStrength = desc->maxStrength;
        auto* result = new AjCharacter{ world, new CharacterVirtual(&settings, ToJolt(desc->position), ToJolt(desc->rotation), 0, &world->system), MakeObjectLayer(desc->layers, desc->mask, AJ_MOTION_DYNAMIC, false) };
        return result;
    }

    APOGEE_JOLT_API void aj_character_destroy(AjCharacter* character) { delete character; }

    APOGEE_JOLT_API void aj_character_step(AjCharacter* character, float deltaTime, AjVec3 velocity)
    {
        character->character->SetLinearVelocity(ToJolt(velocity));
        CharacterVirtual::ExtendedUpdateSettings settings;
        settings.mStickToFloorStepDown = Vec3(0, -0.5f, 0);
        settings.mWalkStairsStepUp = Vec3(0, 0.4f, 0);
        settings.mWalkStairsMinStepForward = 0.02f;
        settings.mWalkStairsStepForwardTest = 0.15f;
        settings.mWalkStairsCosAngleForwardContact = std::cos(75.0f * JPH_PI / 180.0f);
        PhysicsSystem& system = character->world->system;
        character->character->ExtendedUpdate(deltaTime, system.GetGravity(), settings, system.GetDefaultBroadPhaseLayerFilter(character->layer), system.GetDefaultLayerFilter(character->layer), {}, {}, character->world->temp);
    }

    APOGEE_JOLT_API AjRVec3 aj_character_get_position(const AjCharacter* character) { return FromJolt(character->character->GetPosition()); }

    APOGEE_JOLT_API void aj_character_set_position(AjCharacter* character, AjRVec3 position) { character->character->SetPosition(ToJolt(position)); }

    APOGEE_JOLT_API void aj_character_set_rotation(AjCharacter* character, AjQuat rotation) { character->character->SetRotation(ToJolt(rotation)); }

    APOGEE_JOLT_API AjVec3 aj_character_get_velocity(const AjCharacter* character) { return FromJolt(character->character->GetLinearVelocity()); }

    APOGEE_JOLT_API bool aj_character_is_supported(const AjCharacter* character) { return character->character->IsSupported(); }

    APOGEE_JOLT_API AjVec3 aj_character_get_ground_velocity(const AjCharacter* character) { return FromJolt(character->character->GetGroundVelocity()); }
}

// ---- Vehicles ----------------------------------------------------------------------------------------------------------------------

struct AjVehicle
{
    AjWorld* world;
    Ref<VehicleConstraint> constraint;
    bool tracked;
    std::vector<Ref<WheelSettings>> wheels;

    VehicleEngine& Engine() const
    {
        return tracked ? static_cast<TrackedVehicleController*>(constraint->GetController())->GetEngine()
                       : static_cast<WheeledVehicleController*>(constraint->GetController())->GetEngine();
    }

    VehicleTransmission& Transmission() const
    {
        return tracked ? static_cast<TrackedVehicleController*>(constraint->GetController())->GetTransmission()
                       : static_cast<WheeledVehicleController*>(constraint->GetController())->GetTransmission();
    }
};

namespace
{
    void FillEngine(VehicleEngineSettings& engine, const AjVehicleDesc& desc)
    {
        engine.mMaxTorque = desc.engineMaxTorque;
        engine.mMinRPM = desc.engineMinRpm;
        engine.mMaxRPM = desc.engineMaxRpm;
        engine.mInertia = desc.engineInertia;
        engine.mNormalizedTorque.Clear();
        for (int i = 0; i <= 16; i++)
        {
            engine.mNormalizedTorque.AddPoint(i / 16.0f, desc.torqueCurve[i]);
        }
    }

    void FillTransmission(VehicleTransmissionSettings& transmission, const AjVehicleDesc& desc)
    {
        // Manual, one forward and one reverse gear whose ratio the game rewrites every step.
        transmission.mMode = ETransmissionMode::Manual;
        transmission.mGearRatios = { 1.0f };
        transmission.mReverseGearRatios = { -1.0f };
        transmission.mClutchStrength = desc.clutchStrength;
    }

    void ScaleCurve(LinearCurve& curve, float factor)
    {
        for (LinearCurve::Point& point : curve.mPoints)
        {
            point.mY *= factor;
        }
    }
}

extern "C"
{
    APOGEE_JOLT_API AjVehicle* aj_vehicle_create(AjWorld* world, AjBodyId body, const AjVehicleDesc* desc)
    {
        auto* vehicle = new AjVehicle{ world, nullptr, desc->tracked, {} };
        VehicleConstraintSettings settings;
        settings.mUp = Vec3::sAxisY();
        settings.mForward = Vec3::sAxisZ();
        settings.mMaxPitchRollAngle = desc->maxPitchRollRadians;
        for (uint32_t i = 0; i < desc->wheelCount; i++)
        {
            const AjWheelDesc& wheel = desc->wheels[i];
            WheelSettings* common;
            if (desc->tracked)
            {
                auto* tv = new WheelSettingsTV;
                tv->mLongitudinalFriction *= wheel.friction;
                tv->mLateralFriction *= wheel.friction;
                common = tv;
            }
            else
            {
                auto* wv = new WheelSettingsWV;
                wv->mMaxSteerAngle = wheel.maxSteer;
                wv->mMaxBrakeTorque = wheel.maxBrakeTorque;
                wv->mMaxHandBrakeTorque = wheel.maxHandbrakeTorque;
                wv->mInertia = wheel.inertia;
                ScaleCurve(wv->mLongitudinalFriction, wheel.friction);
                ScaleCurve(wv->mLateralFriction, wheel.friction);
                common = wv;
            }

            common->mPosition = ToJolt(wheel.position);
            common->mSuspensionMinLength = 0.0f;
            common->mSuspensionMaxLength = wheel.suspensionMaxLength;
            common->mSuspensionSpring = SpringSettings(ESpringMode::FrequencyAndDamping, wheel.springFrequency, wheel.springDamping);
            common->mRadius = wheel.radius;
            common->mWidth = wheel.width;
            settings.mWheels.push_back(common);
            vehicle->wheels.push_back(common);
        }

        if (desc->tracked)
        {
            Ref<TrackedVehicleControllerSettings> controller = new TrackedVehicleControllerSettings;
            FillEngine(controller->mEngine, *desc);
            FillTransmission(controller->mTransmission, *desc);
            for (int side = 0; side < 2; side++)
            {
                const AjTrackDesc& track = desc->tracks[side];
                VehicleTrackSettings& out = controller->mTracks[side];
                out.mWheels.clear();
                for (uint32_t i = 0; i < track.wheelCount; i++)
                {
                    out.mWheels.push_back(track.wheels[i]);
                }

                out.mDrivenWheel = track.drivenWheel;
                out.mDifferentialRatio = track.ratio;
                out.mMaxBrakeTorque = track.maxBrakeTorque;
            }

            settings.mController = controller;
        }
        else
        {
            Ref<WheeledVehicleControllerSettings> controller = new WheeledVehicleControllerSettings;
            FillEngine(controller->mEngine, *desc);
            FillTransmission(controller->mTransmission, *desc);
            for (uint32_t i = 0; i < desc->differentialCount; i++)
            {
                VehicleDifferentialSettings differential;
                differential.mLeftWheel = desc->differentials[i].leftWheel;
                differential.mRightWheel = desc->differentials[i].rightWheel;
                differential.mDifferentialRatio = desc->differentials[i].ratio;
                differential.mEngineTorqueRatio = desc->differentials[i].engineTorqueRatio;
                controller->mDifferentials.push_back(differential);
            }

            settings.mController = controller;
        }

        {
            BodyLockWrite lock(world->system.GetBodyLockInterface(), Id(body));
            if (!lock.Succeeded())
            {
                delete vehicle;
                return nullptr;
            }

            vehicle->constraint = new VehicleConstraint(lock.GetBody(), settings);
            vehicle->constraint->SetVehicleCollisionTester(new VehicleCollisionTesterCastCylinder(lock.GetBody().GetObjectLayer(), 0.1f));
        }

        world->system.AddConstraint(vehicle->constraint);
        world->system.AddStepListener(vehicle->constraint);
        vehicle->Transmission().Set(0, 0.0f); // neutral
        return vehicle;
    }

    APOGEE_JOLT_API void aj_vehicle_destroy(AjVehicle* vehicle)
    {
        if (vehicle == nullptr)
        {
            return;
        }

        vehicle->world->system.RemoveStepListener(vehicle->constraint);
        vehicle->world->system.RemoveConstraint(vehicle->constraint);
        delete vehicle;
    }

    APOGEE_JOLT_API void aj_vehicle_set_gearbox(AjVehicle* vehicle, float ratio, float clutch)
    {
        VehicleTransmission& transmission = vehicle->Transmission();
        if (std::fabs(ratio) < 1e-6f)
        {
            transmission.Set(0, 0.0f);
            return;
        }

        transmission.mGearRatios[0] = std::fabs(ratio);
        transmission.mReverseGearRatios[0] = -std::fabs(ratio);
        transmission.Set(ratio > 0 ? 1 : -1, std::min(std::max(clutch, 0.0f), 1.0f));
    }

    APOGEE_JOLT_API void aj_vehicle_drive_wheeled(AjVehicle* vehicle, float forward, float brake, float handbrake)
    {
        // The steering is in each wheel's angle, so the controller's input is 1: it sets the wheel to its maximum angle.
        static_cast<WheeledVehicleController*>(vehicle->constraint->GetController())->SetDriverInput(forward, 1.0f, brake, handbrake);
    }

    APOGEE_JOLT_API void aj_vehicle_drive_tracked(AjVehicle* vehicle, float forward, float left, float right, float brake)
    {
        static_cast<TrackedVehicleController*>(vehicle->constraint->GetController())->SetDriverInput(forward, left, right, brake);
    }

    APOGEE_JOLT_API void aj_vehicle_set_wheel_steer(AjVehicle* vehicle, uint32_t wheel, float angleRadians)
    {
        if (!vehicle->tracked && wheel < vehicle->wheels.size())
        {
            static_cast<WheelSettingsWV*>(vehicle->wheels[wheel].GetPtr())->mMaxSteerAngle = angleRadians;
        }
    }

    APOGEE_JOLT_API void aj_vehicle_set_wheel_brake(AjVehicle* vehicle, uint32_t wheel, float torque)
    {
        if (!vehicle->tracked && wheel < vehicle->wheels.size())
        {
            static_cast<WheelSettingsWV*>(vehicle->wheels[wheel].GetPtr())->mMaxBrakeTorque = torque;
        }
    }

    APOGEE_JOLT_API float aj_vehicle_get_engine_rpm(const AjVehicle* vehicle) { return vehicle->Engine().GetCurrentRPM(); }

    APOGEE_JOLT_API float aj_vehicle_get_engine_torque(const AjVehicle* vehicle, float throttle) { return vehicle->Engine().GetTorque(throttle); }

    APOGEE_JOLT_API float aj_vehicle_get_gear_ratio(const AjVehicle* vehicle) { return vehicle->Transmission().GetCurrentRatio(); }

    APOGEE_JOLT_API int32_t aj_vehicle_get_gear(const AjVehicle* vehicle) { return vehicle->Transmission().GetCurrentGear(); }

    APOGEE_JOLT_API bool aj_vehicle_get_wheel_state(const AjVehicle* vehicle, uint32_t index, AjWheelState* state)
    {
        if (index >= vehicle->wheels.size())
        {
            return false;
        }

        const Wheel* wheel = vehicle->constraint->GetWheels()[index];
        std::memset(state, 0, sizeof(*state));
        state->contact = wheel->HasContact();
        state->angularVelocity = wheel->GetAngularVelocity();
        state->steerAngle = wheel->GetSteerAngle();
        state->suspensionLength = wheel->GetSuspensionLength();
        state->suspensionLambda = wheel->GetSuspensionLambda();
        state->contactBody = AJ_INVALID_BODY;
        if (state->contact)
        {
            state->contactPoint = FromJolt(wheel->GetContactPosition());
            state->contactNormal = FromJolt(wheel->GetContactNormal());
            state->contactBody = wheel->GetContactBodyID().GetIndexAndSequenceNumber();
        }

        if (!vehicle->tracked)
        {
            state->grip = static_cast<const WheelSettingsWV*>(vehicle->wheels[index].GetPtr())->mLongitudinalFriction.GetValue(0.1f);
        }

        // Wheel models have their axle along X, and +X is the vehicle's left.
        Mat44 local = vehicle->constraint->GetWheelLocalTransform(index, Vec3(-1, 0, 0), Vec3(0, 1, 0));
        state->localPosition = FromJolt(local.GetTranslation());
        state->localRotation = FromJolt(local.GetQuaternion());
        return true;
    }
}

// ---- A drawbar -----------------------------------------------------------------------------------------------------------------------

struct AjJoint
{
    AjWorld* world;
    Ref<Constraint> constraint;
};

extern "C"
{
    APOGEE_JOLT_API AjJoint* aj_drawbar_create(AjWorld* world, const AjDrawbarDesc* desc)
    {
        SixDOFConstraintSettings settings;
        settings.mSpace = EConstraintSpace::WorldSpace;
        settings.mPosition1 = ToJolt(desc->positionA);
        settings.mAxisX1 = ToJolt(desc->axisXA);
        settings.mAxisY1 = ToJolt(desc->axisYA);
        settings.mPosition2 = ToJolt(desc->positionB);
        settings.mAxisX2 = ToJolt(desc->axisXB);
        settings.mAxisY2 = ToJolt(desc->axisYB);
        settings.MakeFixedAxis(SixDOFConstraintSettings::EAxis::TranslationX);
        settings.MakeFixedAxis(SixDOFConstraintSettings::EAxis::TranslationY);
        settings.MakeFixedAxis(SixDOFConstraintSettings::EAxis::TranslationZ);
        settings.SetLimitedAxis(SixDOFConstraintSettings::EAxis::RotationX, -desc->angularLimits.x, desc->angularLimits.x);
        settings.SetLimitedAxis(SixDOFConstraintSettings::EAxis::RotationY, -desc->angularLimits.y, desc->angularLimits.y);
        settings.SetLimitedAxis(SixDOFConstraintSettings::EAxis::RotationZ, -desc->angularLimits.z, desc->angularLimits.z);
        TwoBodyConstraint* constraint = Bodies(world).CreateConstraint(&settings, Id(desc->bodyA), Id(desc->bodyB));
        if (constraint == nullptr)
        {
            return nullptr;
        }

        world->system.AddConstraint(constraint);
        return new AjJoint{ world, constraint };
    }

    APOGEE_JOLT_API void aj_drawbar_destroy(AjJoint* joint)
    {
        if (joint != nullptr)
        {
            joint->world->system.RemoveConstraint(joint->constraint);
            delete joint;
        }
    }
}

// ---- Debug drawing -----------------------------------------------------------------------------------------------------------------

extern "C"
{
#ifdef JPH_DEBUG_RENDERER
    APOGEE_JOLT_API void aj_debug_clear(void)
    {
        if (g_collector != nullptr)
        {
            g_collector->lines.clear();
            g_collector->triangles.clear();
        }
    }

    APOGEE_JOLT_API uint32_t aj_debug_line_count(void) { return g_collector != nullptr ? (uint32_t)g_collector->lines.size() : 0; }

    APOGEE_JOLT_API const AjDebugLine* aj_debug_lines(void) { return g_collector != nullptr ? g_collector->lines.data() : nullptr; }

    APOGEE_JOLT_API uint32_t aj_debug_triangle_count(void) { return g_collector != nullptr ? (uint32_t)g_collector->triangles.size() : 0; }

    APOGEE_JOLT_API const AjDebugTriangle* aj_debug_triangles(void) { return g_collector != nullptr ? g_collector->triangles.data() : nullptr; }

    APOGEE_JOLT_API void aj_world_draw_debug(AjWorld* world, const AjDebugSettings* settings, AjRVec3 camera)
    {
        DebugCollector& collector = Collector();
        collector.SetCameraPos(RVec3(camera.x, camera.y, camera.z));

        if (settings->shapes || settings->boundingBoxes || settings->centerOfMass || settings->velocities)
        {
            BodyManager::DrawSettings draw;
            draw.mDrawShape = settings->shapes;
            draw.mDrawShapeWireframe = settings->wireframe;
            draw.mDrawShapeColor = settings->colorByMotion ? BodyManager::EShapeColor::MotionTypeColor : BodyManager::EShapeColor::InstanceColor;
            draw.mDrawBoundingBox = settings->boundingBoxes;
            draw.mDrawCenterOfMassTransform = settings->centerOfMass;
            draw.mDrawVelocity = settings->velocities;
            world->system.DrawBodies(draw, &collector);
        }

        if (settings->constraints)
        {
            world->system.DrawConstraints(&collector);
        }

        if (settings->constraintLimits)
        {
            world->system.DrawConstraintLimits(&collector);
        }
    }

    APOGEE_JOLT_API void aj_character_draw_debug(const AjCharacter* character, bool wireframe)
    {
        DebugCollector& collector = Collector();
        const CharacterVirtual& virtualCharacter = *character->character;
        virtualCharacter.GetShape()->Draw(&collector, virtualCharacter.GetCenterOfMassTransform(), Vec3::sReplicate(1.0f), Color::sGreen, false, wireframe);
    }
#else
    // Built without the debug renderer: the collector stays empty and drawing does nothing.
    APOGEE_JOLT_API void aj_debug_clear(void) {}
    APOGEE_JOLT_API uint32_t aj_debug_line_count(void) { return 0; }
    APOGEE_JOLT_API const AjDebugLine* aj_debug_lines(void) { return nullptr; }
    APOGEE_JOLT_API uint32_t aj_debug_triangle_count(void) { return 0; }
    APOGEE_JOLT_API const AjDebugTriangle* aj_debug_triangles(void) { return nullptr; }
    APOGEE_JOLT_API void aj_world_draw_debug(AjWorld*, const AjDebugSettings*, AjRVec3) {}
    APOGEE_JOLT_API void aj_character_draw_debug(const AjCharacter*, bool) {}
#endif
}
