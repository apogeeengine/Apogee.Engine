/*
 * Our C API over Jolt Physics (ThirdParty/jolt). It is written for what the engine needs, not as a mirror of Jolt: a world owns its job system, temp
 * allocator, layer filters and event queue; shapes, bodies, queries, a character controller, vehicles and drawbar joints are handles and plain structs.
 *
 * Built with JPH_DOUBLE_PRECISION (positions are `AjRVec3`, three doubles; rotations, scales and velocities are floats) and JPH_CROSS_PLATFORM_DETERMINISTIC
 * (see Native/jolt/CMakeLists.txt). Collision layers are part of the contract: a body is in `layers` (14 bits, a bit each) and collides with `mask`; two bodies
 * collide when each is in a layer the other's mask holds. Static bodies never collide with each other.
 *
 * Threads: Jolt runs contact and activation listeners on its worker threads inside aj_world_update, where managed code must not run, so they only append to a
 * queue that is drained with aj_events_take after the update. Nothing here is thread-safe except what is said: call it from one thread.
 * Errors: functions that can fail return false or null; none throws. A handle must not be used after it is destroyed.
 */
#ifndef APOGEE_JOLT_H
#define APOGEE_JOLT_H

#include <stdbool.h>
#include <stdint.h>

#ifndef APOGEE_JOLT_API
#define APOGEE_JOLT_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AjVec3 { float x, y, z; } AjVec3;
typedef struct AjQuat { float x, y, z, w; } AjQuat;

/** A position: three doubles (Jolt is built with double precision). */
typedef struct AjRVec3 { double x, y, z; } AjRVec3;

typedef uint32_t AjBodyId;

/** The id of no body. */
#define AJ_INVALID_BODY 0xFFFFFFFFu

/** The collision layers a game can use (bits 0 to 13). */
#define AJ_LAYER_BITS 0x3FFFu

typedef struct AjWorld AjWorld;
typedef struct AjShape AjShape;
typedef struct AjCharacter AjCharacter;
typedef struct AjVehicle AjVehicle;
typedef struct AjJoint AjJoint;

typedef enum AjMotionType
{
    AJ_MOTION_STATIC = 0,
    AJ_MOTION_KINEMATIC = 1,
    AJ_MOTION_DYNAMIC = 2,
} AjMotionType;

/* ---- The library ---- */

/** Registers Jolt's types. Once per process, before anything else; false if it failed. */
APOGEE_JOLT_API bool aj_init(void);

/** Unregisters them. After every world and shape is gone. */
APOGEE_JOLT_API void aj_shutdown(void);

/* ---- The world ---- */

typedef struct AjWorldDesc
{
    uint32_t maxBodies;
    uint32_t maxBodyPairs;
    uint32_t maxContactConstraints;
    /** Bytes of working memory for one update; a step that needs more allocates from the heap. */
    uint32_t tempBufferBytes;
    /** Worker threads of the job system (at least 1). The results do not depend on it. */
    int32_t threads;
    /** Events that can wait between two aj_events_take calls; the rest are dropped and counted. */
    uint32_t eventCapacity;
    uint32_t velocitySteps;
    uint32_t positionSteps;
    AjVec3 gravity;
} AjWorldDesc;

APOGEE_JOLT_API AjWorld* aj_world_create(const AjWorldDesc* desc);

/** Destroys the world with its bodies, constraints, characters and vehicles (shapes live on while someone holds them). */
APOGEE_JOLT_API void aj_world_destroy(AjWorld* world);

/** Steps the simulation by `deltaTime` seconds, as one collision step. Returns 0, or a bit mask of Jolt's EPhysicsUpdateError (1 manifold cache, 2 body pair cache, 4 contact constraints full). */
APOGEE_JOLT_API uint32_t aj_world_update(AjWorld* world, float deltaTime);

APOGEE_JOLT_API void aj_world_set_gravity(AjWorld* world, AjVec3 gravity);
APOGEE_JOLT_API AjVec3 aj_world_get_gravity(const AjWorld* world);

/** Writes the ids of the awake rigid bodies into `ids` (at most `capacity`) and returns how many there are in all. */
APOGEE_JOLT_API uint32_t aj_world_get_active_bodies(const AjWorld* world, AjBodyId* ids, uint32_t capacity);

/* ---- Events ---- */

typedef enum AjEventKind
{
    /** Two bodies started touching (or a body entered a sensor): the position, normal and depth are those of the first contact point. */
    AJ_EVENT_CONTACT_ADDED = 1,
    /** They stopped (also when one of them goes to sleep). Only for pairs whose start was reported; position, normal and depth are zero. */
    AJ_EVENT_CONTACT_REMOVED = 2,
    /** A body woke up: `body1` is it. */
    AJ_EVENT_BODY_ACTIVATED = 3,
    AJ_EVENT_BODY_DEACTIVATED = 4,
} AjEventKind;

/** Set in `flags` of a contact event when one of the bodies is a sensor. */
#define AJ_EVENT_SENSOR 1u

typedef struct AjEvent
{
    int32_t kind;
    AjBodyId body1;
    AjBodyId body2;
    uint32_t flags;
    AjVec3 normal;
    float depth;
    /** The first contact point on body 1. */
    AjRVec3 position;
    uint64_t userData1;
    uint64_t userData2;
} AjEvent;

/** Moves up to `max` events, oldest first, into `out`; returns how many. Between updates, not during one. */
APOGEE_JOLT_API uint32_t aj_events_take(AjWorld* world, AjEvent* out, uint32_t max);

/** How many events did not fit since the last call. Clears the count. */
APOGEE_JOLT_API uint32_t aj_events_dropped(AjWorld* world);

/** Makes two bodies pass through each other (a truck and its trailer). Between updates only. */
APOGEE_JOLT_API void aj_ignore_pair(AjWorld* world, AjBodyId a, AjBodyId b);
APOGEE_JOLT_API void aj_allow_pair(AjWorld* world, AjBodyId a, AjBodyId b);

/* ---- Shapes: reference counted; every aj_shape_* function returns one reference (null on failure) that aj_shape_release gives back ---- */

APOGEE_JOLT_API AjShape* aj_shape_box(AjVec3 halfExtents, float convexRadius);
APOGEE_JOLT_API AjShape* aj_shape_sphere(float radius);
/** Along Y: a cylinder of `halfHeight` (without the caps) and a hemisphere on each end. */
APOGEE_JOLT_API AjShape* aj_shape_capsule(float halfHeight, float radius);
APOGEE_JOLT_API AjShape* aj_shape_cylinder(float halfHeight, float radius);
APOGEE_JOLT_API AjShape* aj_shape_convex_hull(const AjVec3* points, uint32_t count);
/** Three indices per triangle. */
APOGEE_JOLT_API AjShape* aj_shape_mesh(const AjVec3* vertices, uint32_t vertexCount, const uint32_t* indices, uint32_t triangleCount);
/** `size` by `size` heights row by row; a height of 1e30 or more is a hole. */
APOGEE_JOLT_API AjShape* aj_shape_height_field(const float* heights, uint32_t size, AjVec3 offset, AjVec3 scale);
APOGEE_JOLT_API AjShape* aj_shape_static_compound(AjShape* const* children, const AjVec3* positions, const AjQuat* rotations, uint32_t count);
/** The shape moved and turned inside its own space. */
APOGEE_JOLT_API AjShape* aj_shape_offset(AjShape* shape, AjVec3 position, AjQuat rotation);
APOGEE_JOLT_API AjShape* aj_shape_scaled(AjShape* shape, AjVec3 scale);
/** The shape with its center of mass moved by `offset`. */
APOGEE_JOLT_API AjShape* aj_shape_offset_center_of_mass(AjShape* shape, AjVec3 offset);

/** A compound that can change (an attachment mounted on a truck): it starts as `first` at the origin, which is child 0. */
APOGEE_JOLT_API AjShape* aj_mutable_compound(AjShape* first);
/** Adds a child and returns its index. */
APOGEE_JOLT_API uint32_t aj_mutable_compound_add(AjShape* compound, AjShape* child, AjVec3 position, AjQuat rotation);
APOGEE_JOLT_API void aj_mutable_compound_modify(AjShape* compound, uint32_t index, AjVec3 position, AjQuat rotation);
APOGEE_JOLT_API void aj_mutable_compound_remove(AjShape* compound, uint32_t index);
/** Recomputes the center of mass after children changed. */
APOGEE_JOLT_API void aj_mutable_compound_adjust_center_of_mass(AjShape* compound);

APOGEE_JOLT_API void aj_shape_release(AjShape* shape);
APOGEE_JOLT_API float aj_shape_volume(const AjShape* shape);
APOGEE_JOLT_API AjVec3 aj_shape_center_of_mass(const AjShape* shape);
APOGEE_JOLT_API bool aj_shape_is_valid_scale(const AjShape* shape, AjVec3 scale);

/* ---- Bodies ---- */

typedef struct AjBodyDesc
{
    AjShape* shape;
    AjRVec3 position;
    AjQuat rotation;
    AjMotionType motion;
    /** The collision layers the body is in, and the ones it collides with (AJ_LAYER_BITS). */
    uint32_t layers;
    uint32_t mask;
    /** Yours, whole: events report it back. */
    uint64_t userData;
    float friction;
    float restitution;
    float linearDamping;
    float angularDamping;
    float gravityFactor;
    /** A dynamic body's mass in kilograms; 0 takes it from the shape's volume and 1000 kg/m^3. */
    float mass;
    /** Does not collide: reports what is inside it. */
    bool sensor;
    /** Reports its contacts and when it wakes or sleeps (sensors always report contacts). */
    bool events;
    bool allowSleeping;
    /** Sweeps its whole path so a fast body cannot tunnel through a thin wall. */
    bool bullet;
    AjVec3 linearVelocity;
    AjVec3 angularVelocity;
} AjBodyDesc;

/** Makes the body and adds it (awake, unless static). Returns AJ_INVALID_BODY if the world is full. */
APOGEE_JOLT_API AjBodyId aj_body_create(AjWorld* world, const AjBodyDesc* desc);
APOGEE_JOLT_API void aj_body_destroy(AjWorld* world, AjBodyId body);

APOGEE_JOLT_API void aj_body_get_pose(const AjWorld* world, AjBodyId body, AjRVec3* position, AjQuat* rotation);
APOGEE_JOLT_API void aj_body_set_pose(AjWorld* world, AjBodyId body, AjRVec3 position, AjQuat rotation);
/** Moves a kinematic body so that it arrives at the pose after `deltaTime` seconds, pushing what is in the way. */
APOGEE_JOLT_API void aj_body_move_kinematic(AjWorld* world, AjBodyId body, AjRVec3 position, AjQuat rotation, float deltaTime);
APOGEE_JOLT_API AjVec3 aj_body_get_linear_velocity(const AjWorld* world, AjBodyId body);
APOGEE_JOLT_API void aj_body_set_linear_velocity(AjWorld* world, AjBodyId body, AjVec3 velocity);
APOGEE_JOLT_API AjVec3 aj_body_get_angular_velocity(const AjWorld* world, AjBodyId body);
APOGEE_JOLT_API void aj_body_set_angular_velocity(AjWorld* world, AjBodyId body, AjVec3 velocity);
/** For the next update only. */
APOGEE_JOLT_API void aj_body_add_force(AjWorld* world, AjBodyId body, AjVec3 force);
APOGEE_JOLT_API void aj_body_add_torque(AjWorld* world, AjBodyId body, AjVec3 torque);
APOGEE_JOLT_API void aj_body_add_impulse(AjWorld* world, AjBodyId body, AjVec3 impulse);
APOGEE_JOLT_API void aj_body_activate(AjWorld* world, AjBodyId body);
APOGEE_JOLT_API bool aj_body_is_active(const AjWorld* world, AjBodyId body);
/** The body's shape (borrowed: the body holds the reference). */
APOGEE_JOLT_API AjShape* aj_body_get_shape(const AjWorld* world, AjBodyId body);
/** Gives the body another shape; with `updateMass` its mass and inertia follow the shape's volume. */
APOGEE_JOLT_API void aj_body_set_shape(AjWorld* world, AjBodyId body, AjShape* shape, bool updateMass);
/** The shape changed in place (a mutable compound): `previousCenterOfMass` is where its center of mass was. */
APOGEE_JOLT_API void aj_body_notify_shape_changed(AjWorld* world, AjBodyId body, AjVec3 previousCenterOfMass);
/** A dynamic body's mass in kilograms (0 for the others). */
APOGEE_JOLT_API float aj_body_get_mass(const AjWorld* world, AjBodyId body);
/** Sets the mass; the inertia is the body's shape's, scaled to it. */
APOGEE_JOLT_API void aj_body_set_mass(AjWorld* world, AjBodyId body, float mass);

/* ---- Queries: `mask` picks the collision layers that count, `ignore` is a body the query passes through; sensors are never hit ---- */

typedef struct AjRayHit
{
    AjBodyId body;
    /** Of the length of the direction. */
    float fraction;
    AjVec3 normal;
} AjRayHit;

/** Casts a ray from `origin` along `direction` (its length is the range). */
APOGEE_JOLT_API bool aj_raycast(const AjWorld* world, AjRVec3 origin, AjVec3 direction, uint32_t mask, AjBodyId ignore, AjRayHit* hit);

typedef struct AjCastHit
{
    AjBodyId body;
    float fraction;
    AjRVec3 point;
    AjVec3 normal;
} AjCastHit;

/** Sweeps a sphere from `origin` along `direction`; the first body it touches. */
APOGEE_JOLT_API bool aj_cast_sphere(const AjWorld* world, AjRVec3 origin, AjVec3 direction, float radius, uint32_t mask, AjBodyId ignore, AjCastHit* hit);

/** Fills `ids` (at most `capacity`) with the bodies that touch a sphere, each once, and returns how many there are in all. */
APOGEE_JOLT_API uint32_t aj_overlap_sphere(const AjWorld* world, AjRVec3 center, float radius, uint32_t mask, AjBodyId ignore, AjBodyId* ids, uint32_t capacity);
APOGEE_JOLT_API uint32_t aj_overlap_box(const AjWorld* world, AjRVec3 center, AjQuat rotation, AjVec3 halfExtents, uint32_t mask, AjBodyId ignore, AjBodyId* ids, uint32_t capacity);

/* ---- Character controller (Jolt's CharacterVirtual): a capsule that slides, climbs stairs and slopes and sticks to the ground ---- */

typedef struct AjCharacterDesc
{
    /** Feet to top; at least twice the radius. */
    float height;
    float radius;
    float maxSlopeRadians;
    float mass;
    float maxStrength;
    /** The feet. */
    AjRVec3 position;
    AjQuat rotation;
    uint32_t layers;
    uint32_t mask;
} AjCharacterDesc;

APOGEE_JOLT_API AjCharacter* aj_character_create(AjWorld* world, const AjCharacterDesc* desc);
APOGEE_JOLT_API void aj_character_destroy(AjCharacter* character);
/** Moves toward `velocity` (world space, m/s) for `deltaTime`: slides, steps up stairs (up to 0.4 m), sticks to the floor going down. */
APOGEE_JOLT_API void aj_character_step(AjCharacter* character, float deltaTime, AjVec3 velocity);
APOGEE_JOLT_API AjRVec3 aj_character_get_position(const AjCharacter* character);
APOGEE_JOLT_API void aj_character_set_position(AjCharacter* character, AjRVec3 position);
APOGEE_JOLT_API void aj_character_set_rotation(AjCharacter* character, AjQuat rotation);
/** The velocity it actually moved with in the last step. */
APOGEE_JOLT_API AjVec3 aj_character_get_velocity(const AjCharacter* character);
/** Stands on something that holds it (not a slope too steep to stand on). */
APOGEE_JOLT_API bool aj_character_is_supported(const AjCharacter* character);
APOGEE_JOLT_API AjVec3 aj_character_get_ground_velocity(const AjCharacter* character);

/* ---- Vehicles (Jolt's VehicleConstraint, wheeled or tracked) ---- */

typedef struct AjWheelDesc
{
    /** The wheel's attachment point in the body's space: its center with the suspension extended, plus the travel. */
    AjVec3 position;
    float radius;
    float width;
    float suspensionMaxLength;
    float springFrequency;
    float springDamping;
    /** Wheeled: the angle at full steering, radians. */
    float maxSteer;
    float maxBrakeTorque;
    float maxHandbrakeTorque;
    float inertia;
    /** Scales Jolt's tire grip (a track's friction). */
    float friction;
} AjWheelDesc;

typedef struct AjDifferentialDesc
{
    /** Wheel indices; -1 for none. */
    int32_t leftWheel;
    int32_t rightWheel;
    float ratio;
    float engineTorqueRatio;
} AjDifferentialDesc;

typedef struct AjTrackDesc
{
    uint32_t drivenWheel;
    const uint32_t* wheels;
    uint32_t wheelCount;
    float ratio;
    float maxBrakeTorque;
} AjTrackDesc;

typedef struct AjVehicleDesc
{
    bool tracked;
    const AjWheelDesc* wheels;
    uint32_t wheelCount;
    float maxPitchRollRadians;
    float engineMaxTorque;
    float engineMinRpm;
    float engineMaxRpm;
    float engineInertia;
    /** The engine's torque as a share of the maximum at 17 evenly spaced points from 0 to the maximum rpm. */
    float torqueCurve[17];
    /** Torque the clutch carries per rad/s it slips. */
    float clutchStrength;
    /** Wheeled: a differential per axle of powered wheels. */
    const AjDifferentialDesc* differentials;
    uint32_t differentialCount;
    /** Tracked: the left (+X) and right track. */
    AjTrackDesc tracks[2];
} AjVehicleDesc;

typedef struct AjWheelState
{
    bool contact;
    float angularVelocity;
    float steerAngle;
    float suspensionLength;
    /** The suspension's impulse in the last step: divide by the step for newtons. */
    float suspensionLambda;
    /** The tire's longitudinal grip at a slip of 0.1 (0 for a track's wheel). */
    float grip;
    AjRVec3 contactPoint;
    AjVec3 contactNormal;
    AjBodyId contactBody;
    /** Where the wheel is in the body's space, with its axle along -X. */
    AjVec3 localPosition;
    AjQuat localRotation;
} AjWheelState;

/** Makes the vehicle on a dynamic body and adds it to the world; the transmission starts in neutral. */
APOGEE_JOLT_API AjVehicle* aj_vehicle_create(AjWorld* world, AjBodyId body, const AjVehicleDesc* desc);
APOGEE_JOLT_API void aj_vehicle_destroy(AjVehicle* vehicle);

/** The gear the game drives through: a signed overall ratio (0 is neutral) and how far the clutch is engaged (0 to 1). Jolt's transmission keeps one forward and one reverse gear, rewritten here. */
APOGEE_JOLT_API void aj_vehicle_set_gearbox(AjVehicle* vehicle, float ratio, float clutch);
/** Wheeled: the inputs for the next step (the steering is each wheel's `aj_vehicle_set_wheel_steer`). */
APOGEE_JOLT_API void aj_vehicle_drive_wheeled(AjVehicle* vehicle, float forward, float brake, float handbrake);
/** Tracked: the speed ratios of the left and right track (not 0). */
APOGEE_JOLT_API void aj_vehicle_drive_tracked(AjVehicle* vehicle, float forward, float left, float right, float brake);
APOGEE_JOLT_API void aj_vehicle_set_wheel_steer(AjVehicle* vehicle, uint32_t wheel, float angleRadians);
APOGEE_JOLT_API void aj_vehicle_set_wheel_brake(AjVehicle* vehicle, uint32_t wheel, float torque);

APOGEE_JOLT_API float aj_vehicle_get_engine_rpm(const AjVehicle* vehicle);
/** The engine's torque at this throttle (0 to 1), in newton meters. */
APOGEE_JOLT_API float aj_vehicle_get_engine_torque(const AjVehicle* vehicle, float throttle);
APOGEE_JOLT_API float aj_vehicle_get_gear_ratio(const AjVehicle* vehicle);
APOGEE_JOLT_API int32_t aj_vehicle_get_gear(const AjVehicle* vehicle);
APOGEE_JOLT_API bool aj_vehicle_get_wheel_state(const AjVehicle* vehicle, uint32_t wheel, AjWheelState* state);

/* ---- A drawbar: the point of one body held to a point of another, free to swing within limits ---- */

typedef struct AjDrawbarDesc
{
    AjBodyId bodyA;
    AjBodyId bodyB;
    /** The joint's frame on each body, in the world: a position and the X and Y axes. */
    AjRVec3 positionA;
    AjVec3 axisXA;
    AjVec3 axisYA;
    AjRVec3 positionB;
    AjVec3 axisXB;
    AjVec3 axisYB;
    /** The swing about the frame's X, Y and Z, radians either way. */
    AjVec3 angularLimits;
} AjDrawbarDesc;

APOGEE_JOLT_API AjJoint* aj_drawbar_create(AjWorld* world, const AjDrawbarDesc* desc);
APOGEE_JOLT_API void aj_drawbar_destroy(AjJoint* joint);

/* ---- Debug drawing ---- */

/** A line, in world positions, with its color as bytes r, g, b, a (an AjDebugColor read as a little-endian uint32 is 0xAABBGGRR). */
typedef struct AjDebugLine
{
    AjRVec3 from;
    AjRVec3 to;
    uint32_t color;
    uint32_t padding;
} AjDebugLine;

/** A triangle, in world positions, wound counter-clockwise seen from outside, with its color as AjDebugLine's. */
typedef struct AjDebugTriangle
{
    AjRVec3 a;
    AjRVec3 b;
    AjRVec3 c;
    uint32_t color;
    uint32_t padding;
} AjDebugTriangle;

/** What aj_world_draw_debug puts in the collector. */
typedef struct AjDebugSettings
{
    /** Every body's shape. */
    bool shapes;
    /** Shapes as lines (their triangles' edges) instead of solid triangles. */
    bool wireframe;
    /** Color by motion type (static gray, kinematic green, dynamic by island, sleeping darker) instead of one color per body. */
    bool colorByMotion;
    bool boundingBoxes;
    bool centerOfMass;
    bool velocities;
    /** Joints, drawbars and vehicles (wheels, suspension, contact points). */
    bool constraints;
    /** The limits of the joints. */
    bool constraintLimits;
} AjDebugSettings;

/**
 * The lines and triangles that aj_world_draw_debug and aj_character_draw_debug add. There is one collector per process (Jolt allows one debug renderer at a time), made on first use and freed
 * by aj_shutdown. The pointers aj_debug_lines and aj_debug_triangles return stay valid until the next aj_debug_clear or draw call.
 */
APOGEE_JOLT_API void aj_debug_clear(void);
APOGEE_JOLT_API uint32_t aj_debug_line_count(void);
APOGEE_JOLT_API const AjDebugLine* aj_debug_lines(void);
APOGEE_JOLT_API uint32_t aj_debug_triangle_count(void);
APOGEE_JOLT_API const AjDebugTriangle* aj_debug_triangles(void);

/** Adds what `settings` asks for to the collector. `camera` picks the level of detail of round shapes. Call aj_debug_clear first for a fresh picture. */
APOGEE_JOLT_API void aj_world_draw_debug(AjWorld* world, const AjDebugSettings* settings, AjRVec3 camera);

/** Adds the character's capsule (a character is not a body, so the world's drawing leaves it out). */
APOGEE_JOLT_API void aj_character_draw_debug(const AjCharacter* character, bool wireframe);

#ifdef __cplusplus
}
#endif

#endif
