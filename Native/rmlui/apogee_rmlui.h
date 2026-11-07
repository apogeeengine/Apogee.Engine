/*
 * Our C API over RmlUi (ThirdParty/rmlui): the library for game UI. RmlUi is C++ with inheritance and templates, so a game talks to it through this: plain functions, plain structs
 * and integer handles. RmlUi itself is compiled into the same shared library with its symbols hidden; only what is declared here is exported.
 *
 * The host (the engine) gives RmlUi what it cannot do alone through ArHost: drawing (RmlUi hands over geometry and the host draws it), time, logging, the clipboard, reading files
 * and receiving events. Every ArHost function may be NULL (its work is then skipped: no drawing, no files). They are called from inside RmlUi, on the thread that called the API.
 *
 * Elements are handles (ArElement, 64 bits, 0 is none), not pointers: a handle names a slot that watches its element, so a handle to an element that RmlUi has since destroyed is
 * detected and refused, not dereferenced. An element that was created here (ar_document_create_element) is owned by its handle until it is put in a tree; after that the tree owns it.
 * Handles that a call returns, ar_event_target and ar_event_current included, are the caller's to release with ar_element_release.
 *
 * Threads: none of it is thread-safe; call it from one thread. Errors: functions that can fail return false, 0 or -1; none throws or aborts.
 */
#ifndef APOGEE_RMLUI_H
#define APOGEE_RMLUI_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifndef APOGEE_RMLUI_API
#define APOGEE_RMLUI_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef uint64_t ArElement;
typedef struct ArContext ArContext;
typedef struct ArEvent ArEvent;

/** A drawing resource of the host's (compiled geometry, a texture): 0 is none. */
typedef uint64_t ArHandle;

/** One vertex of RmlUi's geometry: a position in pixels, a color with its alpha already multiplied in, and a texture coordinate. 20 bytes, the layout of Rml::Vertex. */
typedef struct ArVertex
{
    float x, y;
    uint8_t r, g, b, a;
    float u, v;
} ArVertex;

/** A rectangle in pixels. */
typedef struct ArRect { float x, y, width, height; } ArRect;

/** The kinds of message RmlUi logs (Rml::Log::Type). */
typedef enum ArLogType
{
    AR_LOG_ALWAYS = 0,
    AR_LOG_ERROR = 1,
    AR_LOG_ASSERT = 2,
    AR_LOG_WARNING = 3,
    AR_LOG_INFO = 4,
    AR_LOG_DEBUG = 5,
} ArLogType;

/** The key modifiers of the input calls: a bit mask. */
typedef enum ArKeyModifier
{
    AR_KM_CTRL = 1,
    AR_KM_SHIFT = 2,
    AR_KM_ALT = 4,
    AR_KM_META = 8,
    AR_KM_CAPSLOCK = 16,
    AR_KM_NUMLOCK = 32,
    AR_KM_SCROLLLOCK = 64,
} ArKeyModifier;

/** The keys of the input calls: the values of Rml::Input::KeyIdentifier, which the library checks at compile time. */
typedef enum ArKey
{
    AR_KEY_UNKNOWN = 0,
    AR_KEY_SPACE = 1,
    AR_KEY_0 = 2,
    AR_KEY_1 = 3,
    AR_KEY_2 = 4,
    AR_KEY_3 = 5,
    AR_KEY_4 = 6,
    AR_KEY_5 = 7,
    AR_KEY_6 = 8,
    AR_KEY_7 = 9,
    AR_KEY_8 = 10,
    AR_KEY_9 = 11,
    AR_KEY_A = 12,
    AR_KEY_B = 13,
    AR_KEY_C = 14,
    AR_KEY_D = 15,
    AR_KEY_E = 16,
    AR_KEY_F = 17,
    AR_KEY_G = 18,
    AR_KEY_H = 19,
    AR_KEY_I = 20,
    AR_KEY_J = 21,
    AR_KEY_K = 22,
    AR_KEY_L = 23,
    AR_KEY_M = 24,
    AR_KEY_N = 25,
    AR_KEY_O = 26,
    AR_KEY_P = 27,
    AR_KEY_Q = 28,
    AR_KEY_R = 29,
    AR_KEY_S = 30,
    AR_KEY_T = 31,
    AR_KEY_U = 32,
    AR_KEY_V = 33,
    AR_KEY_W = 34,
    AR_KEY_X = 35,
    AR_KEY_Y = 36,
    AR_KEY_Z = 37,
    AR_KEY_OEM_1 = 38,
    AR_KEY_OEM_PLUS = 39,
    AR_KEY_OEM_COMMA = 40,
    AR_KEY_OEM_MINUS = 41,
    AR_KEY_OEM_PERIOD = 42,
    AR_KEY_OEM_2 = 43,
    AR_KEY_OEM_3 = 44,
    AR_KEY_OEM_4 = 45,
    AR_KEY_OEM_5 = 46,
    AR_KEY_OEM_6 = 47,
    AR_KEY_OEM_7 = 48,
    AR_KEY_OEM_8 = 49,
    AR_KEY_OEM_102 = 50,
    AR_KEY_NUMPAD0 = 51,
    AR_KEY_NUMPAD1 = 52,
    AR_KEY_NUMPAD2 = 53,
    AR_KEY_NUMPAD3 = 54,
    AR_KEY_NUMPAD4 = 55,
    AR_KEY_NUMPAD5 = 56,
    AR_KEY_NUMPAD6 = 57,
    AR_KEY_NUMPAD7 = 58,
    AR_KEY_NUMPAD8 = 59,
    AR_KEY_NUMPAD9 = 60,
    AR_KEY_NUMPADENTER = 61,
    AR_KEY_MULTIPLY = 62,
    AR_KEY_ADD = 63,
    AR_KEY_SEPARATOR = 64,
    AR_KEY_SUBTRACT = 65,
    AR_KEY_DECIMAL = 66,
    AR_KEY_DIVIDE = 67,
    AR_KEY_OEM_NEC_EQUAL = 68,
    AR_KEY_BACK = 69,
    AR_KEY_TAB = 70,
    AR_KEY_CLEAR = 71,
    AR_KEY_RETURN = 72,
    AR_KEY_PAUSE = 73,
    AR_KEY_CAPITAL = 74,
    AR_KEY_KANA = 75,
    AR_KEY_HANGUL = 76,
    AR_KEY_JUNJA = 77,
    AR_KEY_FINAL = 78,
    AR_KEY_HANJA = 79,
    AR_KEY_KANJI = 80,
    AR_KEY_ESCAPE = 81,
    AR_KEY_CONVERT = 82,
    AR_KEY_NONCONVERT = 83,
    AR_KEY_ACCEPT = 84,
    AR_KEY_MODECHANGE = 85,
    AR_KEY_PRIOR = 86,
    AR_KEY_NEXT = 87,
    AR_KEY_END = 88,
    AR_KEY_HOME = 89,
    AR_KEY_LEFT = 90,
    AR_KEY_UP = 91,
    AR_KEY_RIGHT = 92,
    AR_KEY_DOWN = 93,
    AR_KEY_SELECT = 94,
    AR_KEY_PRINT = 95,
    AR_KEY_EXECUTE = 96,
    AR_KEY_SNAPSHOT = 97,
    AR_KEY_INSERT = 98,
    AR_KEY_DELETE = 99,
    AR_KEY_HELP = 100,
    AR_KEY_LWIN = 101,
    AR_KEY_RWIN = 102,
    AR_KEY_APPS = 103,
    AR_KEY_POWER = 104,
    AR_KEY_SLEEP = 105,
    AR_KEY_WAKE = 106,
    AR_KEY_F1 = 107,
    AR_KEY_F2 = 108,
    AR_KEY_F3 = 109,
    AR_KEY_F4 = 110,
    AR_KEY_F5 = 111,
    AR_KEY_F6 = 112,
    AR_KEY_F7 = 113,
    AR_KEY_F8 = 114,
    AR_KEY_F9 = 115,
    AR_KEY_F10 = 116,
    AR_KEY_F11 = 117,
    AR_KEY_F12 = 118,
    AR_KEY_F13 = 119,
    AR_KEY_F14 = 120,
    AR_KEY_F15 = 121,
    AR_KEY_F16 = 122,
    AR_KEY_F17 = 123,
    AR_KEY_F18 = 124,
    AR_KEY_F19 = 125,
    AR_KEY_F20 = 126,
    AR_KEY_F21 = 127,
    AR_KEY_F22 = 128,
    AR_KEY_F23 = 129,
    AR_KEY_F24 = 130,
    AR_KEY_NUMLOCK = 131,
    AR_KEY_SCROLL = 132,
    AR_KEY_OEM_FJ_JISHO = 133,
    AR_KEY_OEM_FJ_MASSHOU = 134,
    AR_KEY_OEM_FJ_TOUROKU = 135,
    AR_KEY_OEM_FJ_LOYA = 136,
    AR_KEY_OEM_FJ_ROYA = 137,
    AR_KEY_LSHIFT = 138,
    AR_KEY_RSHIFT = 139,
    AR_KEY_LCONTROL = 140,
    AR_KEY_RCONTROL = 141,
    AR_KEY_LMENU = 142,
    AR_KEY_RMENU = 143,
    AR_KEY_BROWSER_BACK = 144,
    AR_KEY_BROWSER_FORWARD = 145,
    AR_KEY_BROWSER_REFRESH = 146,
    AR_KEY_BROWSER_STOP = 147,
    AR_KEY_BROWSER_SEARCH = 148,
    AR_KEY_BROWSER_FAVORITES = 149,
    AR_KEY_BROWSER_HOME = 150,
    AR_KEY_VOLUME_MUTE = 151,
    AR_KEY_VOLUME_DOWN = 152,
    AR_KEY_VOLUME_UP = 153,
    AR_KEY_MEDIA_NEXT_TRACK = 154,
    AR_KEY_MEDIA_PREV_TRACK = 155,
    AR_KEY_MEDIA_STOP = 156,
    AR_KEY_MEDIA_PLAY_PAUSE = 157,
    AR_KEY_LAUNCH_MAIL = 158,
    AR_KEY_LAUNCH_MEDIA_SELECT = 159,
    AR_KEY_LAUNCH_APP1 = 160,
    AR_KEY_LAUNCH_APP2 = 161,
    AR_KEY_OEM_AX = 162,
    AR_KEY_ICO_HELP = 163,
    AR_KEY_ICO_00 = 164,
    AR_KEY_PROCESSKEY = 165,
    AR_KEY_ICO_CLEAR = 166,
    AR_KEY_ATTN = 167,
    AR_KEY_CRSEL = 168,
    AR_KEY_EXSEL = 169,
    AR_KEY_EREOF = 170,
    AR_KEY_PLAY = 171,
    AR_KEY_ZOOM = 172,
    AR_KEY_PA1 = 173,
    AR_KEY_OEM_CLEAR = 174,
    AR_KEY_LMETA = 175,
    AR_KEY_RMETA = 176,
    AR_KEY_FIRST_CUSTOM_KEY = 177,
    AR_KEY_LAST_CUSTOM_KEY = 250,
} ArKey;

/** The phase an event is in (Rml::Event::EventPhase). */
typedef enum ArEventPhase
{
    AR_PHASE_NONE = 0,
    AR_PHASE_CAPTURE = 1,
    AR_PHASE_TARGET = 2,
    AR_PHASE_BUBBLE = 4,
} ArEventPhase;

/** Receives bytes from the host while a host function runs (see ArHost.read_file); the library copies them. */
APOGEE_RMLUI_API void ar_sink_write(void* sink, const void* data, size_t size);

/* ---- Values and data models ---- */

/** The kinds of ArVariant. */
typedef enum ArVariantType
{
    AR_VARIANT_NIL = 0,
    AR_VARIANT_BOOL = 1,
    AR_VARIANT_INT = 2,
    AR_VARIANT_DOUBLE = 3,
    AR_VARIANT_STRING = 4,
} ArVariantType;

/** A value that crosses between RmlUi's data models and the host: `integer` holds a bool (0 or 1) or an integer, `number` a floating point value, `text` UTF-8 that is valid for the duration of the call. */
typedef struct ArVariant
{
    int32_t type;
    int32_t reserved;
    int64_t integer;
    double number;
    const char* text;
} ArVariant;

/** What a data variable is (RmlUi's DataVariableType): a value, a list indexed from 0, or something with named members. 0 means there is nothing there. */
typedef enum ArDataKind
{
    AR_DATA_MISSING = 0,
    AR_DATA_SCALAR = 1,
    AR_DATA_ARRAY = 2,
    AR_DATA_STRUCT = 3,
} ArDataKind;

/* ---- Effects ---- */

/** The kinds of filter the host can be asked to compile (ArFilterDesc.kind). */
typedef enum ArFilterKind
{
    /** A blur: `sigma` is the standard deviation in pixels. */
    AR_FILTER_BLUR = 1,
    /** A drop shadow: the layer's shape tinted `color`, offset by `offsetX`, `offsetY` and blurred by `sigma`, under the layer. */
    AR_FILTER_DROP_SHADOW = 2,
    /** A color transform: opacity, brightness, contrast, invert, grayscale, sepia, saturate and hue-rotate all arrive as a 4x5 matrix. */
    AR_FILTER_COLOR_MATRIX = 3,
} ArFilterKind;

/**
 * A filter as RmlUi's `filter` and `backdrop-filter` properties describe it. `matrix` is row-major, 4 rows (r, g, b, a) of 5 columns (r, g, b, a, offset), applied to the color with its alpha
 * not multiplied in, in 0 to 1; `color` is straight RGBA.
 */
typedef struct ArFilterDesc
{
    int32_t kind;
    float sigma;
    float offsetX, offsetY;
    uint8_t color[4];
    float matrix[20];
} ArFilterDesc;

/** The kinds of shader (ArShaderDesc.kind): the three gradients. */
typedef enum ArShaderKind
{
    AR_SHADER_LINEAR_GRADIENT = 1,
    AR_SHADER_RADIAL_GRADIENT = 2,
    AR_SHADER_CONIC_GRADIENT = 3,
} ArShaderKind;

/** A color stop with its color's alpha multiplied in, at `position` along the gradient (0 to 1 over its length; more for a repeating one). */
typedef struct ArColorStop
{
    uint8_t r, g, b, a;
    float position;
} ArColorStop;

/**
 * A gradient. Geometry drawn with it carries the position inside the element's box, in pixels, as its texture coordinate. Linear: `p0` to `p1` (`length` is their distance). Radial: `center` and
 * `radius` (x and y: an ellipse). Conic: `center` and `angle` (radians, clockwise from up). `repeating` repeats the stops. `stops` is valid during the call.
 */
typedef struct ArShaderDesc
{
    int32_t kind;
    float p0x, p0y, p1x, p1y;
    float length;
    float centerX, centerY;
    float radiusX, radiusY;
    float angle;
    int32_t repeating;
    int32_t stopCount;
    const ArColorStop* stops;
} ArShaderDesc;

/** How geometry drawn to the clip mask changes it (Rml::ClipMaskOperation). */
typedef enum ArClipOperation
{
    /** The mask becomes the area of the geometry. */
    AR_CLIP_SET = 0,
    /** The mask becomes the area outside the geometry. */
    AR_CLIP_SET_INVERSE = 1,
    /** The mask keeps what is inside the geometry. */
    AR_CLIP_INTERSECT = 2,
} ArClipOperation;

/* ---- The host ---- */

typedef struct ArHost
{
    /** sizeof(ArHost) as the caller knows it: a newer library reads only what an older caller has. */
    uint32_t size;
    /** Passed to every function below as its first argument. */
    void* user;

    /* Drawing. Geometry is drawn as indexed triangles in the order of the calls, with the scissor and transform that were last set. */
    ArHandle (*compile_geometry)(void* user, const ArVertex* vertices, int32_t vertexCount, const int32_t* indices, int32_t indexCount);
    void (*render_geometry)(void* user, ArHandle geometry, float translationX, float translationY, ArHandle texture);
    void (*release_geometry)(void* user, ArHandle geometry);
    /** Loads the image at `source` (a path as the document wrote it, resolved by RmlUi against the document's), returns its texture and writes its size, or returns 0. */
    ArHandle (*load_texture)(void* user, const char* source, int32_t* width, int32_t* height);
    /** A texture from `width` x `height` pixels of RGBA8 with the alpha multiplied in (font atlases and decorators). */
    ArHandle (*generate_texture)(void* user, const uint8_t* rgba, int32_t width, int32_t height);
    void (*release_texture)(void* user, ArHandle texture);
    void (*enable_scissor)(void* user, int32_t enable);
    /** The scissor rectangle in pixels, origin at the top left. */
    void (*set_scissor)(void* user, int32_t x, int32_t y, int32_t width, int32_t height);
    /** The transform of the geometry that follows: 16 floats, column-major, or NULL for none. */
    void (*set_transform)(void* user, const float* matrix);

    /* The system. */
    /** Seconds since some point; without it the library's own clock is used. */
    double (*get_elapsed_time)(void* user);
    void (*log)(void* user, int32_t type, const char* message);
    void (*set_mouse_cursor)(void* user, const char* name);
    void (*set_clipboard_text)(void* user, const char* text);
    /** Writes the clipboard's text to `sink` with ar_sink_write. */
    void (*get_clipboard_text)(void* user, void* sink);

    /** Writes the whole file at `path` (what RmlUi asked for: a document, a stylesheet, a font) to `sink` with ar_sink_write and returns 1, or returns 0 when it cannot be read. */
    int32_t (*read_file)(void* user, const char* path, void* sink);

    /** An event reached a listener (ar_element_add_listener): `token` is the one it returned. `event` is valid until this returns. */
    void (*on_event)(void* user, uint64_t token, ArEvent* event);

    /* Scripts in documents. A document has an id, the same for every call about it until `document_closed`; `document` is a new handle for it that the host owns (ar_element_release it, or keep it). */

    /** A `<script>` with its text in the document (`source` is the document's path, `line` where the script starts). */
    void (*load_inline_script)(void* user, uint64_t documentId, ArElement document, const char* source, const char* content, int32_t line);
    /** A `<script src="...">`; `path` is resolved against the document's. */
    void (*load_external_script)(void* user, uint64_t documentId, ArElement document, const char* path);
    /** An on* attribute was read (by a document being parsed or by a script setting RML): its listener is `token`, and the host can note who is running now. Compiled later, by compile_inline_handler. */
    void (*instance_inline_handler)(void* user, uint64_t token);
    /**
     * An `onclick="code"` attribute (or any on* one) on an element of the document: compile `code`, when the first such event happens; it and the later ones reach `on_event` with `token`. `document` is a new handle
     * the host owns. Returns 0 when it cannot run. The listener goes with its element, as ar_element_add_listener's does.
     */
    int32_t (*compile_inline_handler)(void* user, uint64_t documentId, ArElement document, uint64_t token, const char* code);
    /** The document is gone: everything the host kept for its id can go. */
    void (*document_closed)(void* user, uint64_t documentId);

    /* Data models (ar_data_model_*). A `ref` names a variable of the host's, such as the field of a table: RmlUi hands them back to ask for values; 0 is none. */

    /** Reads a scalar into `value`; 0 if there is none. */
    int32_t (*data_get)(void* user, uint64_t ref, ArVariant* value);
    /** Writes a scalar (a form control changed); 0 if it cannot. */
    int32_t (*data_set)(void* user, uint64_t ref, const ArVariant* value);
    /** The number of elements of an array. */
    int32_t (*data_size)(void* user, uint64_t ref);
    /** The member `name` of a struct, or element `index` (from 0) of an array when `name` is NULL. Returns its ref and writes its ArDataKind to `kind`, or returns 0. */
    uint64_t (*data_child)(void* user, uint64_t ref, const char* name, int32_t index, int32_t* kind);
    /** A `data-event-*` of an element called an event bound with ar_data_model_bind_event: `args` are the arguments the expression gave. */
    void (*data_event)(void* user, uint64_t token, ArEvent* event, const ArVariant* args, int32_t argCount);

    /* Effects. A host that leaves compile_filter and compile_shader out gets no layers, filters or gradients (RmlUi draws without them); one without enable_clip_mask has only rectangular clipping. */

    /** Geometry drawn from now on is clipped to the clip mask (or not). */
    void (*enable_clip_mask)(void* user, int32_t enable);
    /** Changes the clip mask with the geometry, translated, under the transform and scissor that are set (an ArClipOperation). */
    void (*render_to_clip_mask)(void* user, int32_t operation, ArHandle geometry, float translationX, float translationY);
    /** Starts a layer on top of the stack, transparent, that drawing goes to from now on; returns its handle (not 0: 0 is the base layer, what the frame draws to). */
    uint64_t (*push_layer)(void* user);
    /** Draws the layer `source` onto the layer `destination` after the filters (`filterCount` handles from compile_filter or save_layer_as_mask_image, in order), blending over (blendMode 0) or replacing (1). */
    void (*composite_layers)(void* user, uint64_t source, uint64_t destination, int32_t blendMode, const ArHandle* filters, int32_t filterCount);
    /** Ends the top layer. */
    void (*pop_layer)(void* user);
    /** A texture with the scissor region of the top layer as it is now; the host owns it until release_texture. */
    ArHandle (*save_layer_as_texture)(void* user);
    /** A filter that keeps what is inside the shape drawn to the top layer, which a composite applies (a `mask-image`). Released with release_filter. */
    ArHandle (*save_layer_as_mask_image)(void* user);
    ArHandle (*compile_filter)(void* user, const ArFilterDesc* desc);
    void (*release_filter)(void* user, ArHandle filter);
    ArHandle (*compile_shader)(void* user, const ArShaderDesc* desc);
    /** Draws the geometry with the shader (`texture` is the one the geometry would use, 0 for none). */
    void (*render_shader)(void* user, ArHandle shader, ArHandle geometry, float translationX, float translationY, ArHandle texture);
    void (*release_shader)(void* user, ArHandle shader);
} ArHost;

/* ---- The library ---- */

/** Sets the host and starts RmlUi. Once, before anything else; false if it failed. `host` is copied. */
APOGEE_RMLUI_API bool ar_initialize(const ArHost* host);

/** Closes every context and document and stops RmlUi. Every handle is invalid afterwards. */
APOGEE_RMLUI_API void ar_shutdown(void);

/**
 * The stylesheet (RCSS text) that goes under the rules of every document loaded afterwards with ar_context_load_document*: RmlUi has no user-agent stylesheet, so a host gives it one. A document's own rules win.
 * NULL or empty removes it. False if the text does not parse.
 */
APOGEE_RMLUI_API bool ar_set_base_style(const char* rcss);

/** RmlUi's version as text, for logs. */
APOGEE_RMLUI_API const char* ar_version(void);

/**
 * Loads a font from memory (TTF or OTF). `family` is the name documents use (NULL: the font's own); the face is the regular one unless `bold` or `italic`. A fallback face is
 * used for characters the others lack. False if it is not a font FreeType reads.
 */
APOGEE_RMLUI_API bool ar_load_font(const uint8_t* data, size_t size, const char* family, bool italic, bool bold, bool fallback);

/* ---- Contexts ---- */

/** A context is a screen of documents with its own size and input. Destroy it before ar_shutdown, or let ar_shutdown do it. */
APOGEE_RMLUI_API ArContext* ar_context_create(const char* name, int32_t width, int32_t height);
APOGEE_RMLUI_API void ar_context_destroy(ArContext* context);
APOGEE_RMLUI_API void ar_context_set_size(ArContext* context, int32_t width, int32_t height);
APOGEE_RMLUI_API void ar_context_set_density(ArContext* context, float pixelsPerDp);

/** Lays out what changed and runs animations; call it once a frame before ar_context_render. Returns true when something changed. */
APOGEE_RMLUI_API bool ar_context_update(ArContext* context);

/** Hands the host the geometry of every visible document, in order, through the ArHost drawing functions. */
APOGEE_RMLUI_API bool ar_context_render(ArContext* context);

/** Loads a document from RML text; `sourceUrl` is what relative paths in it resolve against. 0 on failure (the log says why). */
APOGEE_RMLUI_API ArElement ar_context_load_document_text(ArContext* context, const char* rml, const char* sourceUrl);

/** Loads a document from a file, read through ArHost.read_file. */
APOGEE_RMLUI_API ArElement ar_context_load_document(ArContext* context, const char* path);

/** Unloads the document and everything in it; its handle and those of its elements become invalid. */
APOGEE_RMLUI_API void ar_context_unload_document(ArContext* context, ArElement document);

APOGEE_RMLUI_API void ar_document_show(ArElement document);
APOGEE_RMLUI_API void ar_document_hide(ArElement document);

/**
 * Adds the style rules in `rcss` to the document's: on top of what it has (they win where specificity ties), or with `underneath` below it (a base stylesheet that the document's own rules override).
 */
APOGEE_RMLUI_API bool ar_document_add_style(ArElement document, const char* rcss, bool underneath);

/** The document an element belongs to (a document is its own), or 0. */
APOGEE_RMLUI_API ArElement ar_element_document(ArElement element);

/** Creates an element (`tag` such as "div") or a text node that no tree holds yet; its handle owns it until ar_element_append_child or ar_element_insert_before takes it. 0 on failure. */
APOGEE_RMLUI_API ArElement ar_document_create_element(ArElement document, const char* tag);
APOGEE_RMLUI_API ArElement ar_document_create_text(ArElement document, const char* text);

/** The element under a point of the context (in pixels), or 0; and whether the pointer is over an element of a document (not over the empty screen). */
APOGEE_RMLUI_API ArElement ar_context_element_at(ArContext* context, float x, float y);
APOGEE_RMLUI_API bool ar_context_is_mouse_over_ui(ArContext* context);
APOGEE_RMLUI_API ArElement ar_context_focused_element(ArContext* context);

/* ---- Input ----
 * Each returns false when a document consumed the input (a button was clicked, a text field took the key): the game should then leave it alone. */

APOGEE_RMLUI_API bool ar_context_mouse_move(ArContext* context, int32_t x, int32_t y, int32_t modifiers);
APOGEE_RMLUI_API bool ar_context_mouse_button(ArContext* context, int32_t button, bool down, int32_t modifiers);
APOGEE_RMLUI_API bool ar_context_mouse_wheel(ArContext* context, float x, float y, int32_t modifiers);
APOGEE_RMLUI_API bool ar_context_mouse_leave(ArContext* context);
APOGEE_RMLUI_API bool ar_context_key(ArContext* context, int32_t key, bool down, int32_t modifiers);
/** Typed text, UTF-8. */
APOGEE_RMLUI_API bool ar_context_text(ArContext* context, const char* text);

/* ---- Elements ---- */

/** Releases a handle (the element lives on if a tree holds it; an element no tree ever held is destroyed). */
APOGEE_RMLUI_API void ar_element_release(ArElement element);

/** Whether the handle names an element that still exists. */
APOGEE_RMLUI_API bool ar_element_is_alive(ArElement element);

/** Puts `child` (a handle that owns its element) last in `parent`, which owns it from now on. False if `child` is not owned or either is dead. */
APOGEE_RMLUI_API bool ar_element_append_child(ArElement parent, ArElement child);

/** The same, before `before` (a child of `parent`; 0 for last). */
APOGEE_RMLUI_API bool ar_element_insert_before(ArElement parent, ArElement child, ArElement before);

/** Takes `child` out of `parent`: its handle owns it again, to put somewhere else or destroy. False if it is not a child of `parent`. */
APOGEE_RMLUI_API bool ar_element_remove_child(ArElement parent, ArElement child);

/** Destroys the element: out of its tree if it is in one, with all its children. Its handle (and every other for it) is dead afterwards. */
APOGEE_RMLUI_API void ar_element_destroy(ArElement element);

APOGEE_RMLUI_API ArElement ar_element_parent(ArElement element);
APOGEE_RMLUI_API int32_t ar_element_child_count(ArElement element);
APOGEE_RMLUI_API ArElement ar_element_child(ArElement element, int32_t index);

/** The first element below `root` with that id, or 0. */
APOGEE_RMLUI_API ArElement ar_element_by_id(ArElement root, const char* id);

/** The nearest element above (and including) this one that matches the selector, or 0. */
APOGEE_RMLUI_API ArElement ar_element_closest(ArElement element, const char* selector);

/** Whether `inner` is `outer` or below it. */
APOGEE_RMLUI_API bool ar_element_contains(ArElement outer, ArElement inner);

/* The text results below are written to `buffer` as UTF-8 with a zero at the end (cut to `capacity`); the return value is the length the whole text needs, without the zero, or -1. */
APOGEE_RMLUI_API int32_t ar_element_tag(ArElement element, char* buffer, int32_t capacity);
APOGEE_RMLUI_API int32_t ar_element_get_id(ArElement element, char* buffer, int32_t capacity);
APOGEE_RMLUI_API bool ar_element_set_id(ArElement element, const char* id);
APOGEE_RMLUI_API int32_t ar_element_get_class_names(ArElement element, char* buffer, int32_t capacity);
APOGEE_RMLUI_API bool ar_element_set_class_names(ArElement element, const char* classNames);
APOGEE_RMLUI_API int32_t ar_element_get_attribute(ArElement element, const char* name, char* buffer, int32_t capacity);
APOGEE_RMLUI_API bool ar_element_set_attribute(ArElement element, const char* name, const char* value);
APOGEE_RMLUI_API bool ar_element_remove_attribute(ArElement element, const char* name);
APOGEE_RMLUI_API bool ar_element_has_attribute(ArElement element, const char* name);

/** Sets an inline style property (as RCSS: "width", "100px"); false if the name or value is not valid. */
APOGEE_RMLUI_API bool ar_element_set_property(ArElement element, const char* name, const char* value);
APOGEE_RMLUI_API bool ar_element_remove_property(ArElement element, const char* name);

APOGEE_RMLUI_API int32_t ar_element_get_inner_rml(ArElement element, char* buffer, int32_t capacity);
APOGEE_RMLUI_API bool ar_element_set_inner_rml(ArElement element, const char* rml);

/** The text of a text node (the result of ar_document_create_text); false (or -1) for any other element. */
APOGEE_RMLUI_API bool ar_text_set(ArElement text, const char* value);
APOGEE_RMLUI_API int32_t ar_text_get(ArElement text, char* buffer, int32_t capacity);

APOGEE_RMLUI_API bool ar_element_set_pseudo_class(ArElement element, const char* name, bool on);
APOGEE_RMLUI_API bool ar_element_is_pseudo_class_set(ArElement element, const char* name);

APOGEE_RMLUI_API bool ar_element_focus(ArElement element);
APOGEE_RMLUI_API void ar_element_blur(ArElement element);
APOGEE_RMLUI_API void ar_element_click(ArElement element);
APOGEE_RMLUI_API void ar_element_scroll_into_view(ArElement element, bool alignWithTop);

/** The element's border box in the context, in pixels, as the last ar_context_update laid it out. */
APOGEE_RMLUI_API bool ar_element_get_rect(ArElement element, ArRect* rect);
/** Whether it is shown (not display: none or visibility: hidden, itself or above). */
APOGEE_RMLUI_API bool ar_element_is_visible(ArElement element);

/* ---- Events ---- */

/**
 * Listens for events of the type (`click`, `change`, `keydown`...) on the element, in the capture phase or on the way back up. Returns a token for ar_element_remove_listener and the
 * one ArHost.on_event is called with, or 0. The listener goes when the element is destroyed.
 */
APOGEE_RMLUI_API uint64_t ar_element_add_listener(ArElement element, const char* type, bool capture);
APOGEE_RMLUI_API bool ar_element_remove_listener(uint64_t token);
/** Whether the listener still exists (it is gone when its element is destroyed or ar_element_remove_listener removed it). */
APOGEE_RMLUI_API bool ar_listener_exists(uint64_t token);

APOGEE_RMLUI_API int32_t ar_event_type(const ArEvent* event, char* buffer, int32_t capacity);
/** The element the event is about, and the one whose listener is running: new handles, the caller's to release like any other (the event itself is valid only during the callback). */
APOGEE_RMLUI_API ArElement ar_event_target(ArEvent* event);
APOGEE_RMLUI_API ArElement ar_event_current(ArEvent* event);
APOGEE_RMLUI_API int32_t ar_event_phase(const ArEvent* event);
APOGEE_RMLUI_API void ar_event_stop_propagation(ArEvent* event);
APOGEE_RMLUI_API bool ar_event_has_parameter(const ArEvent* event, const char* key);
APOGEE_RMLUI_API int32_t ar_event_get_string(const ArEvent* event, const char* key, char* buffer, int32_t capacity);
APOGEE_RMLUI_API double ar_event_get_number(const ArEvent* event, const char* key, double fallback);

/* ---- Data models ----
 * A data model ties values of the host to a document: `<p data-model="stats">{{score}}</p>`, `data-if`, `data-for`, `data-value`, `data-event-click="buy(1)"`. Create the model before the
 * document that uses it is parsed (a script in the document's head does), bind its variables, and mark a variable dirty when its value changed.
 * Values are read through ArHost.data_* by ref, when RmlUi needs them. Models are addressed by name within a context.
 */

/** Creates the model. False if there is one of that name already. */
APOGEE_RMLUI_API bool ar_data_model_create(ArContext* context, const char* model);
/** Binds a top-level variable of the model, of the kind `kind` (not AR_DATA_MISSING). */
APOGEE_RMLUI_API bool ar_data_model_bind(ArContext* context, const char* model, const char* name, uint64_t ref, int32_t kind);
/** Binds an event for `data-event-*`: ArHost.data_event is called with `token`. */
APOGEE_RMLUI_API bool ar_data_model_bind_event(ArContext* context, const char* model, const char* name, uint64_t token);
/** The value of the variable (NULL: every one) changed: the elements that show it are updated at the next ar_context_update. */
APOGEE_RMLUI_API bool ar_data_model_dirty(ArContext* context, const char* model, const char* name);
APOGEE_RMLUI_API bool ar_data_model_is_dirty(ArContext* context, const char* model, const char* name);
/** Removes the model; documents that use it must be closed first. */
APOGEE_RMLUI_API bool ar_data_model_remove(ArContext* context, const char* model);

/* ---- The debugger ---- */

/** RmlUi's inspector (element tree, styles, log) as a window in `context`. */
APOGEE_RMLUI_API bool ar_debugger_initialize(ArContext* context);
APOGEE_RMLUI_API void ar_debugger_set_visible(bool visible);
APOGEE_RMLUI_API bool ar_debugger_is_visible(void);

#ifdef __cplusplus
}
#endif

#endif
