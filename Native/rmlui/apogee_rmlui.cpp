// Our C API over RmlUi: see apogee_rmlui.h.

#include "apogee_rmlui.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <unordered_map>
#include <vector>

#include <RmlUi/Core.h>
#include <RmlUi/Core/ElementText.h>
#include <RmlUi/Core/DecorationTypes.h>
#include <RmlUi/Core/ElementInstancer.h>
#include <RmlUi/Core/EventListenerInstancer.h>
#include <RmlUi/Core/Factory.h>
#include <RmlUi/Debugger.h>

// The values the header promises are the ones RmlUi has.
static_assert(sizeof(ArVertex) == sizeof(Rml::Vertex), "Rml::Vertex is 20 bytes");
static_assert(offsetof(ArVertex, a) == 11 && offsetof(ArVertex, u) == 12, "the layout of Rml::Vertex");
static_assert((int)Rml::Log::LT_ALWAYS == AR_LOG_ALWAYS && (int)Rml::Log::LT_ERROR == AR_LOG_ERROR && (int)Rml::Log::LT_ASSERT == AR_LOG_ASSERT
    && (int)Rml::Log::LT_WARNING == AR_LOG_WARNING && (int)Rml::Log::LT_INFO == AR_LOG_INFO && (int)Rml::Log::LT_DEBUG == AR_LOG_DEBUG, "Rml::Log::Type");
static_assert((int)Rml::Input::KM_CTRL == AR_KM_CTRL && (int)Rml::Input::KM_SHIFT == AR_KM_SHIFT && (int)Rml::Input::KM_ALT == AR_KM_ALT && (int)Rml::Input::KM_META == AR_KM_META
    && (int)Rml::Input::KM_CAPSLOCK == AR_KM_CAPSLOCK && (int)Rml::Input::KM_NUMLOCK == AR_KM_NUMLOCK && (int)Rml::Input::KM_SCROLLLOCK == AR_KM_SCROLLLOCK, "Rml::Input::KeyModifier");
static_assert((int)Rml::EventPhase::None == AR_PHASE_NONE && (int)Rml::EventPhase::Capture == AR_PHASE_CAPTURE
    && (int)Rml::EventPhase::Target == AR_PHASE_TARGET && (int)Rml::EventPhase::Bubble == AR_PHASE_BUBBLE, "Rml::Event::EventPhase");
static_assert((int)Rml::Input::KI_UNKNOWN == AR_KEY_UNKNOWN, "KI_UNKNOWN");
static_assert((int)Rml::Input::KI_SPACE == AR_KEY_SPACE, "KI_SPACE");
static_assert((int)Rml::Input::KI_0 == AR_KEY_0, "KI_0");
static_assert((int)Rml::Input::KI_1 == AR_KEY_1, "KI_1");
static_assert((int)Rml::Input::KI_2 == AR_KEY_2, "KI_2");
static_assert((int)Rml::Input::KI_3 == AR_KEY_3, "KI_3");
static_assert((int)Rml::Input::KI_4 == AR_KEY_4, "KI_4");
static_assert((int)Rml::Input::KI_5 == AR_KEY_5, "KI_5");
static_assert((int)Rml::Input::KI_6 == AR_KEY_6, "KI_6");
static_assert((int)Rml::Input::KI_7 == AR_KEY_7, "KI_7");
static_assert((int)Rml::Input::KI_8 == AR_KEY_8, "KI_8");
static_assert((int)Rml::Input::KI_9 == AR_KEY_9, "KI_9");
static_assert((int)Rml::Input::KI_A == AR_KEY_A, "KI_A");
static_assert((int)Rml::Input::KI_B == AR_KEY_B, "KI_B");
static_assert((int)Rml::Input::KI_C == AR_KEY_C, "KI_C");
static_assert((int)Rml::Input::KI_D == AR_KEY_D, "KI_D");
static_assert((int)Rml::Input::KI_E == AR_KEY_E, "KI_E");
static_assert((int)Rml::Input::KI_F == AR_KEY_F, "KI_F");
static_assert((int)Rml::Input::KI_G == AR_KEY_G, "KI_G");
static_assert((int)Rml::Input::KI_H == AR_KEY_H, "KI_H");
static_assert((int)Rml::Input::KI_I == AR_KEY_I, "KI_I");
static_assert((int)Rml::Input::KI_J == AR_KEY_J, "KI_J");
static_assert((int)Rml::Input::KI_K == AR_KEY_K, "KI_K");
static_assert((int)Rml::Input::KI_L == AR_KEY_L, "KI_L");
static_assert((int)Rml::Input::KI_M == AR_KEY_M, "KI_M");
static_assert((int)Rml::Input::KI_N == AR_KEY_N, "KI_N");
static_assert((int)Rml::Input::KI_O == AR_KEY_O, "KI_O");
static_assert((int)Rml::Input::KI_P == AR_KEY_P, "KI_P");
static_assert((int)Rml::Input::KI_Q == AR_KEY_Q, "KI_Q");
static_assert((int)Rml::Input::KI_R == AR_KEY_R, "KI_R");
static_assert((int)Rml::Input::KI_S == AR_KEY_S, "KI_S");
static_assert((int)Rml::Input::KI_T == AR_KEY_T, "KI_T");
static_assert((int)Rml::Input::KI_U == AR_KEY_U, "KI_U");
static_assert((int)Rml::Input::KI_V == AR_KEY_V, "KI_V");
static_assert((int)Rml::Input::KI_W == AR_KEY_W, "KI_W");
static_assert((int)Rml::Input::KI_X == AR_KEY_X, "KI_X");
static_assert((int)Rml::Input::KI_Y == AR_KEY_Y, "KI_Y");
static_assert((int)Rml::Input::KI_Z == AR_KEY_Z, "KI_Z");
static_assert((int)Rml::Input::KI_OEM_1 == AR_KEY_OEM_1, "KI_OEM_1");
static_assert((int)Rml::Input::KI_OEM_PLUS == AR_KEY_OEM_PLUS, "KI_OEM_PLUS");
static_assert((int)Rml::Input::KI_OEM_COMMA == AR_KEY_OEM_COMMA, "KI_OEM_COMMA");
static_assert((int)Rml::Input::KI_OEM_MINUS == AR_KEY_OEM_MINUS, "KI_OEM_MINUS");
static_assert((int)Rml::Input::KI_OEM_PERIOD == AR_KEY_OEM_PERIOD, "KI_OEM_PERIOD");
static_assert((int)Rml::Input::KI_OEM_2 == AR_KEY_OEM_2, "KI_OEM_2");
static_assert((int)Rml::Input::KI_OEM_3 == AR_KEY_OEM_3, "KI_OEM_3");
static_assert((int)Rml::Input::KI_OEM_4 == AR_KEY_OEM_4, "KI_OEM_4");
static_assert((int)Rml::Input::KI_OEM_5 == AR_KEY_OEM_5, "KI_OEM_5");
static_assert((int)Rml::Input::KI_OEM_6 == AR_KEY_OEM_6, "KI_OEM_6");
static_assert((int)Rml::Input::KI_OEM_7 == AR_KEY_OEM_7, "KI_OEM_7");
static_assert((int)Rml::Input::KI_OEM_8 == AR_KEY_OEM_8, "KI_OEM_8");
static_assert((int)Rml::Input::KI_OEM_102 == AR_KEY_OEM_102, "KI_OEM_102");
static_assert((int)Rml::Input::KI_NUMPAD0 == AR_KEY_NUMPAD0, "KI_NUMPAD0");
static_assert((int)Rml::Input::KI_NUMPAD1 == AR_KEY_NUMPAD1, "KI_NUMPAD1");
static_assert((int)Rml::Input::KI_NUMPAD2 == AR_KEY_NUMPAD2, "KI_NUMPAD2");
static_assert((int)Rml::Input::KI_NUMPAD3 == AR_KEY_NUMPAD3, "KI_NUMPAD3");
static_assert((int)Rml::Input::KI_NUMPAD4 == AR_KEY_NUMPAD4, "KI_NUMPAD4");
static_assert((int)Rml::Input::KI_NUMPAD5 == AR_KEY_NUMPAD5, "KI_NUMPAD5");
static_assert((int)Rml::Input::KI_NUMPAD6 == AR_KEY_NUMPAD6, "KI_NUMPAD6");
static_assert((int)Rml::Input::KI_NUMPAD7 == AR_KEY_NUMPAD7, "KI_NUMPAD7");
static_assert((int)Rml::Input::KI_NUMPAD8 == AR_KEY_NUMPAD8, "KI_NUMPAD8");
static_assert((int)Rml::Input::KI_NUMPAD9 == AR_KEY_NUMPAD9, "KI_NUMPAD9");
static_assert((int)Rml::Input::KI_NUMPADENTER == AR_KEY_NUMPADENTER, "KI_NUMPADENTER");
static_assert((int)Rml::Input::KI_MULTIPLY == AR_KEY_MULTIPLY, "KI_MULTIPLY");
static_assert((int)Rml::Input::KI_ADD == AR_KEY_ADD, "KI_ADD");
static_assert((int)Rml::Input::KI_SEPARATOR == AR_KEY_SEPARATOR, "KI_SEPARATOR");
static_assert((int)Rml::Input::KI_SUBTRACT == AR_KEY_SUBTRACT, "KI_SUBTRACT");
static_assert((int)Rml::Input::KI_DECIMAL == AR_KEY_DECIMAL, "KI_DECIMAL");
static_assert((int)Rml::Input::KI_DIVIDE == AR_KEY_DIVIDE, "KI_DIVIDE");
static_assert((int)Rml::Input::KI_OEM_NEC_EQUAL == AR_KEY_OEM_NEC_EQUAL, "KI_OEM_NEC_EQUAL");
static_assert((int)Rml::Input::KI_BACK == AR_KEY_BACK, "KI_BACK");
static_assert((int)Rml::Input::KI_TAB == AR_KEY_TAB, "KI_TAB");
static_assert((int)Rml::Input::KI_CLEAR == AR_KEY_CLEAR, "KI_CLEAR");
static_assert((int)Rml::Input::KI_RETURN == AR_KEY_RETURN, "KI_RETURN");
static_assert((int)Rml::Input::KI_PAUSE == AR_KEY_PAUSE, "KI_PAUSE");
static_assert((int)Rml::Input::KI_CAPITAL == AR_KEY_CAPITAL, "KI_CAPITAL");
static_assert((int)Rml::Input::KI_KANA == AR_KEY_KANA, "KI_KANA");
static_assert((int)Rml::Input::KI_HANGUL == AR_KEY_HANGUL, "KI_HANGUL");
static_assert((int)Rml::Input::KI_JUNJA == AR_KEY_JUNJA, "KI_JUNJA");
static_assert((int)Rml::Input::KI_FINAL == AR_KEY_FINAL, "KI_FINAL");
static_assert((int)Rml::Input::KI_HANJA == AR_KEY_HANJA, "KI_HANJA");
static_assert((int)Rml::Input::KI_KANJI == AR_KEY_KANJI, "KI_KANJI");
static_assert((int)Rml::Input::KI_ESCAPE == AR_KEY_ESCAPE, "KI_ESCAPE");
static_assert((int)Rml::Input::KI_CONVERT == AR_KEY_CONVERT, "KI_CONVERT");
static_assert((int)Rml::Input::KI_NONCONVERT == AR_KEY_NONCONVERT, "KI_NONCONVERT");
static_assert((int)Rml::Input::KI_ACCEPT == AR_KEY_ACCEPT, "KI_ACCEPT");
static_assert((int)Rml::Input::KI_MODECHANGE == AR_KEY_MODECHANGE, "KI_MODECHANGE");
static_assert((int)Rml::Input::KI_PRIOR == AR_KEY_PRIOR, "KI_PRIOR");
static_assert((int)Rml::Input::KI_NEXT == AR_KEY_NEXT, "KI_NEXT");
static_assert((int)Rml::Input::KI_END == AR_KEY_END, "KI_END");
static_assert((int)Rml::Input::KI_HOME == AR_KEY_HOME, "KI_HOME");
static_assert((int)Rml::Input::KI_LEFT == AR_KEY_LEFT, "KI_LEFT");
static_assert((int)Rml::Input::KI_UP == AR_KEY_UP, "KI_UP");
static_assert((int)Rml::Input::KI_RIGHT == AR_KEY_RIGHT, "KI_RIGHT");
static_assert((int)Rml::Input::KI_DOWN == AR_KEY_DOWN, "KI_DOWN");
static_assert((int)Rml::Input::KI_SELECT == AR_KEY_SELECT, "KI_SELECT");
static_assert((int)Rml::Input::KI_PRINT == AR_KEY_PRINT, "KI_PRINT");
static_assert((int)Rml::Input::KI_EXECUTE == AR_KEY_EXECUTE, "KI_EXECUTE");
static_assert((int)Rml::Input::KI_SNAPSHOT == AR_KEY_SNAPSHOT, "KI_SNAPSHOT");
static_assert((int)Rml::Input::KI_INSERT == AR_KEY_INSERT, "KI_INSERT");
static_assert((int)Rml::Input::KI_DELETE == AR_KEY_DELETE, "KI_DELETE");
static_assert((int)Rml::Input::KI_HELP == AR_KEY_HELP, "KI_HELP");
static_assert((int)Rml::Input::KI_LWIN == AR_KEY_LWIN, "KI_LWIN");
static_assert((int)Rml::Input::KI_RWIN == AR_KEY_RWIN, "KI_RWIN");
static_assert((int)Rml::Input::KI_APPS == AR_KEY_APPS, "KI_APPS");
static_assert((int)Rml::Input::KI_POWER == AR_KEY_POWER, "KI_POWER");
static_assert((int)Rml::Input::KI_SLEEP == AR_KEY_SLEEP, "KI_SLEEP");
static_assert((int)Rml::Input::KI_WAKE == AR_KEY_WAKE, "KI_WAKE");
static_assert((int)Rml::Input::KI_F1 == AR_KEY_F1, "KI_F1");
static_assert((int)Rml::Input::KI_F2 == AR_KEY_F2, "KI_F2");
static_assert((int)Rml::Input::KI_F3 == AR_KEY_F3, "KI_F3");
static_assert((int)Rml::Input::KI_F4 == AR_KEY_F4, "KI_F4");
static_assert((int)Rml::Input::KI_F5 == AR_KEY_F5, "KI_F5");
static_assert((int)Rml::Input::KI_F6 == AR_KEY_F6, "KI_F6");
static_assert((int)Rml::Input::KI_F7 == AR_KEY_F7, "KI_F7");
static_assert((int)Rml::Input::KI_F8 == AR_KEY_F8, "KI_F8");
static_assert((int)Rml::Input::KI_F9 == AR_KEY_F9, "KI_F9");
static_assert((int)Rml::Input::KI_F10 == AR_KEY_F10, "KI_F10");
static_assert((int)Rml::Input::KI_F11 == AR_KEY_F11, "KI_F11");
static_assert((int)Rml::Input::KI_F12 == AR_KEY_F12, "KI_F12");
static_assert((int)Rml::Input::KI_F13 == AR_KEY_F13, "KI_F13");
static_assert((int)Rml::Input::KI_F14 == AR_KEY_F14, "KI_F14");
static_assert((int)Rml::Input::KI_F15 == AR_KEY_F15, "KI_F15");
static_assert((int)Rml::Input::KI_F16 == AR_KEY_F16, "KI_F16");
static_assert((int)Rml::Input::KI_F17 == AR_KEY_F17, "KI_F17");
static_assert((int)Rml::Input::KI_F18 == AR_KEY_F18, "KI_F18");
static_assert((int)Rml::Input::KI_F19 == AR_KEY_F19, "KI_F19");
static_assert((int)Rml::Input::KI_F20 == AR_KEY_F20, "KI_F20");
static_assert((int)Rml::Input::KI_F21 == AR_KEY_F21, "KI_F21");
static_assert((int)Rml::Input::KI_F22 == AR_KEY_F22, "KI_F22");
static_assert((int)Rml::Input::KI_F23 == AR_KEY_F23, "KI_F23");
static_assert((int)Rml::Input::KI_F24 == AR_KEY_F24, "KI_F24");
static_assert((int)Rml::Input::KI_NUMLOCK == AR_KEY_NUMLOCK, "KI_NUMLOCK");
static_assert((int)Rml::Input::KI_SCROLL == AR_KEY_SCROLL, "KI_SCROLL");
static_assert((int)Rml::Input::KI_OEM_FJ_JISHO == AR_KEY_OEM_FJ_JISHO, "KI_OEM_FJ_JISHO");
static_assert((int)Rml::Input::KI_OEM_FJ_MASSHOU == AR_KEY_OEM_FJ_MASSHOU, "KI_OEM_FJ_MASSHOU");
static_assert((int)Rml::Input::KI_OEM_FJ_TOUROKU == AR_KEY_OEM_FJ_TOUROKU, "KI_OEM_FJ_TOUROKU");
static_assert((int)Rml::Input::KI_OEM_FJ_LOYA == AR_KEY_OEM_FJ_LOYA, "KI_OEM_FJ_LOYA");
static_assert((int)Rml::Input::KI_OEM_FJ_ROYA == AR_KEY_OEM_FJ_ROYA, "KI_OEM_FJ_ROYA");
static_assert((int)Rml::Input::KI_LSHIFT == AR_KEY_LSHIFT, "KI_LSHIFT");
static_assert((int)Rml::Input::KI_RSHIFT == AR_KEY_RSHIFT, "KI_RSHIFT");
static_assert((int)Rml::Input::KI_LCONTROL == AR_KEY_LCONTROL, "KI_LCONTROL");
static_assert((int)Rml::Input::KI_RCONTROL == AR_KEY_RCONTROL, "KI_RCONTROL");
static_assert((int)Rml::Input::KI_LMENU == AR_KEY_LMENU, "KI_LMENU");
static_assert((int)Rml::Input::KI_RMENU == AR_KEY_RMENU, "KI_RMENU");
static_assert((int)Rml::Input::KI_BROWSER_BACK == AR_KEY_BROWSER_BACK, "KI_BROWSER_BACK");
static_assert((int)Rml::Input::KI_BROWSER_FORWARD == AR_KEY_BROWSER_FORWARD, "KI_BROWSER_FORWARD");
static_assert((int)Rml::Input::KI_BROWSER_REFRESH == AR_KEY_BROWSER_REFRESH, "KI_BROWSER_REFRESH");
static_assert((int)Rml::Input::KI_BROWSER_STOP == AR_KEY_BROWSER_STOP, "KI_BROWSER_STOP");
static_assert((int)Rml::Input::KI_BROWSER_SEARCH == AR_KEY_BROWSER_SEARCH, "KI_BROWSER_SEARCH");
static_assert((int)Rml::Input::KI_BROWSER_FAVORITES == AR_KEY_BROWSER_FAVORITES, "KI_BROWSER_FAVORITES");
static_assert((int)Rml::Input::KI_BROWSER_HOME == AR_KEY_BROWSER_HOME, "KI_BROWSER_HOME");
static_assert((int)Rml::Input::KI_VOLUME_MUTE == AR_KEY_VOLUME_MUTE, "KI_VOLUME_MUTE");
static_assert((int)Rml::Input::KI_VOLUME_DOWN == AR_KEY_VOLUME_DOWN, "KI_VOLUME_DOWN");
static_assert((int)Rml::Input::KI_VOLUME_UP == AR_KEY_VOLUME_UP, "KI_VOLUME_UP");
static_assert((int)Rml::Input::KI_MEDIA_NEXT_TRACK == AR_KEY_MEDIA_NEXT_TRACK, "KI_MEDIA_NEXT_TRACK");
static_assert((int)Rml::Input::KI_MEDIA_PREV_TRACK == AR_KEY_MEDIA_PREV_TRACK, "KI_MEDIA_PREV_TRACK");
static_assert((int)Rml::Input::KI_MEDIA_STOP == AR_KEY_MEDIA_STOP, "KI_MEDIA_STOP");
static_assert((int)Rml::Input::KI_MEDIA_PLAY_PAUSE == AR_KEY_MEDIA_PLAY_PAUSE, "KI_MEDIA_PLAY_PAUSE");
static_assert((int)Rml::Input::KI_LAUNCH_MAIL == AR_KEY_LAUNCH_MAIL, "KI_LAUNCH_MAIL");
static_assert((int)Rml::Input::KI_LAUNCH_MEDIA_SELECT == AR_KEY_LAUNCH_MEDIA_SELECT, "KI_LAUNCH_MEDIA_SELECT");
static_assert((int)Rml::Input::KI_LAUNCH_APP1 == AR_KEY_LAUNCH_APP1, "KI_LAUNCH_APP1");
static_assert((int)Rml::Input::KI_LAUNCH_APP2 == AR_KEY_LAUNCH_APP2, "KI_LAUNCH_APP2");
static_assert((int)Rml::Input::KI_OEM_AX == AR_KEY_OEM_AX, "KI_OEM_AX");
static_assert((int)Rml::Input::KI_ICO_HELP == AR_KEY_ICO_HELP, "KI_ICO_HELP");
static_assert((int)Rml::Input::KI_ICO_00 == AR_KEY_ICO_00, "KI_ICO_00");
static_assert((int)Rml::Input::KI_PROCESSKEY == AR_KEY_PROCESSKEY, "KI_PROCESSKEY");
static_assert((int)Rml::Input::KI_ICO_CLEAR == AR_KEY_ICO_CLEAR, "KI_ICO_CLEAR");
static_assert((int)Rml::Input::KI_ATTN == AR_KEY_ATTN, "KI_ATTN");
static_assert((int)Rml::Input::KI_CRSEL == AR_KEY_CRSEL, "KI_CRSEL");
static_assert((int)Rml::Input::KI_EXSEL == AR_KEY_EXSEL, "KI_EXSEL");
static_assert((int)Rml::Input::KI_EREOF == AR_KEY_EREOF, "KI_EREOF");
static_assert((int)Rml::Input::KI_PLAY == AR_KEY_PLAY, "KI_PLAY");
static_assert((int)Rml::Input::KI_ZOOM == AR_KEY_ZOOM, "KI_ZOOM");
static_assert((int)Rml::Input::KI_PA1 == AR_KEY_PA1, "KI_PA1");
static_assert((int)Rml::Input::KI_OEM_CLEAR == AR_KEY_OEM_CLEAR, "KI_OEM_CLEAR");
static_assert((int)Rml::Input::KI_LMETA == AR_KEY_LMETA, "KI_LMETA");
static_assert((int)Rml::Input::KI_RMETA == AR_KEY_RMETA, "KI_RMETA");
static_assert((int)Rml::Input::KI_FIRST_CUSTOM_KEY == AR_KEY_FIRST_CUSTOM_KEY, "KI_FIRST_CUSTOM_KEY");
static_assert((int)Rml::Input::KI_LAST_CUSTOM_KEY == AR_KEY_LAST_CUSTOM_KEY, "KI_LAST_CUSTOM_KEY");

struct ArEvent
{
    Rml::Event* event;
};

namespace
{
    // ---- The host --------------------------------------------------------------------------------------------------------------------

    ArHost g_host = {};
    bool g_initialized = false;

    struct Sink
    {
        std::vector<uint8_t>* bytes;
    };

    class HostRenderInterface final : public Rml::RenderInterface
    {
    public:
        Rml::CompiledGeometryHandle CompileGeometry(Rml::Span<const Rml::Vertex> vertices, Rml::Span<const int> indices) override
        {
            if (g_host.compile_geometry == nullptr)
                return 0;
            return g_host.compile_geometry(g_host.user, reinterpret_cast<const ArVertex*>(vertices.data()), (int32_t)vertices.size(), indices.data(), (int32_t)indices.size());
        }

        void RenderGeometry(Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation, Rml::TextureHandle texture) override
        {
            if (g_host.render_geometry != nullptr)
                g_host.render_geometry(g_host.user, geometry, translation.x, translation.y, texture);
        }

        void ReleaseGeometry(Rml::CompiledGeometryHandle geometry) override
        {
            if (g_host.release_geometry != nullptr)
                g_host.release_geometry(g_host.user, geometry);
        }

        Rml::TextureHandle LoadTexture(Rml::Vector2i& dimensions, const Rml::String& source) override
        {
            if (g_host.load_texture == nullptr)
                return 0;
            int32_t width = 0, height = 0;
            Rml::TextureHandle texture = g_host.load_texture(g_host.user, source.c_str(), &width, &height);
            dimensions = Rml::Vector2i(width, height);
            return texture;
        }

        Rml::TextureHandle GenerateTexture(Rml::Span<const Rml::byte> source, Rml::Vector2i dimensions) override
        {
            if (g_host.generate_texture == nullptr)
                return 0;
            return g_host.generate_texture(g_host.user, source.data(), dimensions.x, dimensions.y);
        }

        void ReleaseTexture(Rml::TextureHandle texture) override
        {
            if (g_host.release_texture != nullptr)
                g_host.release_texture(g_host.user, texture);
        }

        void EnableScissorRegion(bool enable) override
        {
            if (g_host.enable_scissor != nullptr)
                g_host.enable_scissor(g_host.user, enable ? 1 : 0);
        }

        void SetScissorRegion(Rml::Rectanglei region) override
        {
            if (g_host.set_scissor != nullptr)
                g_host.set_scissor(g_host.user, region.Left(), region.Top(), region.Width(), region.Height());
        }

        void SetTransform(const Rml::Matrix4f* transform) override
        {
            if (g_host.set_transform != nullptr)
                g_host.set_transform(g_host.user, transform != nullptr ? transform->data() : nullptr);
        }

        // ---- Effects: the base class does nothing and RmlUi then draws without them ----

        void EnableClipMask(bool enable) override
        {
            if (g_host.enable_clip_mask != nullptr)
                g_host.enable_clip_mask(g_host.user, enable ? 1 : 0);
        }

        void RenderToClipMask(Rml::ClipMaskOperation operation, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation) override
        {
            if (g_host.render_to_clip_mask != nullptr)
                g_host.render_to_clip_mask(g_host.user, (int32_t)operation, geometry, translation.x, translation.y);
        }

        Rml::LayerHandle PushLayer() override { return g_host.push_layer != nullptr ? (Rml::LayerHandle)g_host.push_layer(g_host.user) : 0; }

        void CompositeLayers(Rml::LayerHandle source, Rml::LayerHandle destination, Rml::BlendMode blendMode, Rml::Span<const Rml::CompiledFilterHandle> filters) override
        {
            if (g_host.composite_layers == nullptr)
                return;
            std::vector<ArHandle> handles(filters.begin(), filters.end());
            g_host.composite_layers(g_host.user, source, destination, blendMode == Rml::BlendMode::Replace ? 1 : 0, handles.data(), (int32_t)handles.size());
        }

        void PopLayer() override
        {
            if (g_host.pop_layer != nullptr)
                g_host.pop_layer(g_host.user);
        }

        Rml::TextureHandle SaveLayerAsTexture() override { return g_host.save_layer_as_texture != nullptr ? (Rml::TextureHandle)g_host.save_layer_as_texture(g_host.user) : 0; }

        Rml::CompiledFilterHandle SaveLayerAsMaskImage() override { return g_host.save_layer_as_mask_image != nullptr ? (Rml::CompiledFilterHandle)g_host.save_layer_as_mask_image(g_host.user) : 0; }

        Rml::CompiledFilterHandle CompileFilter(const Rml::String& name, const Rml::Dictionary& parameters) override;

        void ReleaseFilter(Rml::CompiledFilterHandle filter) override
        {
            if (g_host.release_filter != nullptr)
                g_host.release_filter(g_host.user, filter);
        }

        Rml::CompiledShaderHandle CompileShader(const Rml::String& name, const Rml::Dictionary& parameters) override;

        void RenderShader(Rml::CompiledShaderHandle shader, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation, Rml::TextureHandle texture) override
        {
            if (g_host.render_shader != nullptr)
                g_host.render_shader(g_host.user, shader, geometry, translation.x, translation.y, texture);
        }

        void ReleaseShader(Rml::CompiledShaderHandle shader) override
        {
            if (g_host.release_shader != nullptr)
                g_host.release_shader(g_host.user, shader);
        }
    };

    // The CSS filter functions as 4x5 color matrices (r, g, b, a rows; r, g, b, a, offset columns), on a color whose alpha is not multiplied in (the host does that before and after).
    void IdentityMatrix(float (&m)[20])
    {
        std::memset(m, 0, sizeof(m));
        m[0] = m[6] = m[12] = m[18] = 1.f;
    }

    bool ColorMatrixFor(const Rml::String& name, float value, float (&m)[20])
    {
        IdentityMatrix(m);
        auto rgb = [&](int row, float r, float g, float b) { m[(row * 5) + 0] = r; m[(row * 5) + 1] = g; m[(row * 5) + 2] = b; };
        const float v = value;
        if (name == "opacity")
        {
            m[18] = v;
        }
        else if (name == "brightness")
        {
            m[0] = m[6] = m[12] = v;
        }
        else if (name == "contrast")
        {
            m[0] = m[6] = m[12] = v;
            m[4] = m[9] = m[14] = 0.5f - (0.5f * v);
        }
        else if (name == "invert")
        {
            const float s = 1.f - (2.f * v);
            m[0] = m[6] = m[12] = s;
            m[4] = m[9] = m[14] = v;
        }
        else if (name == "grayscale")
        {
            const float a = 1.f - v;
            rgb(0, 0.2126f + (0.7874f * a), 0.7152f - (0.7152f * a), 0.0722f - (0.0722f * a));
            rgb(1, 0.2126f - (0.2126f * a), 0.7152f + (0.2848f * a), 0.0722f - (0.0722f * a));
            rgb(2, 0.2126f - (0.2126f * a), 0.7152f - (0.7152f * a), 0.0722f + (0.9278f * a));
        }
        else if (name == "sepia")
        {
            const float a = 1.f - v;
            rgb(0, 0.393f + (0.607f * a), 0.769f - (0.769f * a), 0.189f - (0.189f * a));
            rgb(1, 0.349f - (0.349f * a), 0.686f + (0.314f * a), 0.168f - (0.168f * a));
            rgb(2, 0.272f - (0.272f * a), 0.534f - (0.534f * a), 0.131f + (0.869f * a));
        }
        else if (name == "saturate")
        {
            rgb(0, 0.213f + (0.787f * v), 0.715f - (0.715f * v), 0.072f - (0.072f * v));
            rgb(1, 0.213f - (0.213f * v), 0.715f + (0.285f * v), 0.072f - (0.072f * v));
            rgb(2, 0.213f - (0.213f * v), 0.715f - (0.715f * v), 0.072f + (0.928f * v));
        }
        else if (name == "hue-rotate")
        {
            const float c = std::cos(v), s = std::sin(v);
            rgb(0, 0.213f + (c * 0.787f) - (s * 0.213f), 0.715f - (c * 0.715f) - (s * 0.715f), 0.072f - (c * 0.072f) + (s * 0.928f));
            rgb(1, 0.213f - (c * 0.213f) + (s * 0.143f), 0.715f + (c * 0.285f) + (s * 0.140f), 0.072f - (c * 0.072f) - (s * 0.283f));
            rgb(2, 0.213f - (c * 0.213f) - (s * 0.787f), 0.715f - (c * 0.715f) + (s * 0.715f), 0.072f + (c * 0.928f) + (s * 0.072f));
        }
        else
        {
            return false;
        }

        return true;
    }

    template <typename T>
    T ParameterOr(const Rml::Dictionary& parameters, const char* key, T fallback)
    {
        if (const Rml::Variant* variant = Rml::GetIf(parameters, key))
        {
            T value = fallback;
            if (variant->GetInto(value))
                return value;
        }
        return fallback;
    }

    Rml::CompiledFilterHandle HostRenderInterface::CompileFilter(const Rml::String& name, const Rml::Dictionary& parameters)
    {
        if (g_host.compile_filter == nullptr)
            return 0;
        ArFilterDesc desc = {};
        if (name == "blur")
        {
            desc.kind = AR_FILTER_BLUR;
            desc.sigma = ParameterOr<float>(parameters, "sigma", 0.f);
        }
        else if (name == "drop-shadow")
        {
            desc.kind = AR_FILTER_DROP_SHADOW;
            desc.sigma = ParameterOr<float>(parameters, "sigma", 0.f);
            Rml::Vector2f offset = ParameterOr<Rml::Vector2f>(parameters, "offset", Rml::Vector2f(0.f, 0.f));
            desc.offsetX = offset.x;
            desc.offsetY = offset.y;
            Rml::Colourb color = ParameterOr<Rml::Colourb>(parameters, "color", Rml::Colourb(0, 0, 0, 255));
            desc.color[0] = color.red;
            desc.color[1] = color.green;
            desc.color[2] = color.blue;
            desc.color[3] = color.alpha;
        }
        else if (ColorMatrixFor(name, ParameterOr<float>(parameters, "value", 1.f), desc.matrix))
        {
            desc.kind = AR_FILTER_COLOR_MATRIX;
        }
        else
        {
            return 0;
        }
        return (Rml::CompiledFilterHandle)g_host.compile_filter(g_host.user, &desc);
    }

    Rml::CompiledShaderHandle HostRenderInterface::CompileShader(const Rml::String& name, const Rml::Dictionary& parameters)
    {
        if (g_host.compile_shader == nullptr)
            return 0;
        ArShaderDesc desc = {};
        if (name == "linear-gradient")
        {
            desc.kind = AR_SHADER_LINEAR_GRADIENT;
            Rml::Vector2f p0 = ParameterOr<Rml::Vector2f>(parameters, "p0", Rml::Vector2f(0.f, 0.f));
            Rml::Vector2f p1 = ParameterOr<Rml::Vector2f>(parameters, "p1", Rml::Vector2f(0.f, 0.f));
            desc.p0x = p0.x; desc.p0y = p0.y; desc.p1x = p1.x; desc.p1y = p1.y;
            desc.length = ParameterOr<float>(parameters, "length", 1.f);
        }
        else if (name == "radial-gradient")
        {
            desc.kind = AR_SHADER_RADIAL_GRADIENT;
            Rml::Vector2f center = ParameterOr<Rml::Vector2f>(parameters, "center", Rml::Vector2f(0.f, 0.f));
            Rml::Vector2f radius = ParameterOr<Rml::Vector2f>(parameters, "radius", Rml::Vector2f(1.f, 1.f));
            desc.centerX = center.x; desc.centerY = center.y; desc.radiusX = radius.x; desc.radiusY = radius.y;
        }
        else if (name == "conic-gradient")
        {
            desc.kind = AR_SHADER_CONIC_GRADIENT;
            Rml::Vector2f center = ParameterOr<Rml::Vector2f>(parameters, "center", Rml::Vector2f(0.f, 0.f));
            desc.centerX = center.x; desc.centerY = center.y;
            desc.angle = ParameterOr<float>(parameters, "angle", 0.f);
        }
        else
        {
            return 0;
        }
        desc.repeating = ParameterOr<bool>(parameters, "repeating", false) ? 1 : 0;

        std::vector<ArColorStop> stops;
        if (const Rml::Variant* variant = Rml::GetIf(parameters, "color_stop_list"))
        {
            if (variant->GetType() == Rml::Variant::COLORSTOPLIST)
            {
                const Rml::ColorStopList list = variant->Get<Rml::ColorStopList>();
                for (const Rml::ColorStop& stop : list)
                    stops.push_back(ArColorStop{ stop.color.red, stop.color.green, stop.color.blue, stop.color.alpha, stop.position.number });
            }
        }
        if (stops.empty())
            return 0;
        desc.stopCount = (int32_t)stops.size();
        desc.stops = stops.data();
        return (Rml::CompiledShaderHandle)g_host.compile_shader(g_host.user, &desc);
    }

    class HostSystemInterface final : public Rml::SystemInterface
    {
    public:
        double GetElapsedTime() override
        {
            if (g_host.get_elapsed_time != nullptr)
                return g_host.get_elapsed_time(g_host.user);
            static const auto start = std::chrono::steady_clock::now();
            return std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        }

        bool LogMessage(Rml::Log::Type type, const Rml::String& message) override
        {
            if (g_host.log != nullptr)
                g_host.log(g_host.user, (int32_t)type, message.c_str());
            return true;
        }

        void SetMouseCursor(const Rml::String& cursorName) override
        {
            if (g_host.set_mouse_cursor != nullptr)
                g_host.set_mouse_cursor(g_host.user, cursorName.c_str());
        }

        void SetClipboardText(const Rml::String& text) override
        {
            if (g_host.set_clipboard_text != nullptr)
                g_host.set_clipboard_text(g_host.user, text.c_str());
        }

        void GetClipboardText(Rml::String& text) override
        {
            if (g_host.get_clipboard_text == nullptr)
                return;
            std::vector<uint8_t> bytes;
            Sink sink{ &bytes };
            g_host.get_clipboard_text(g_host.user, &sink);
            text.assign(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        }
    };

    // A file is read whole by the host and served from memory: RmlUi reads, seeks and measures it.
    class HostFileInterface final : public Rml::FileInterface
    {
        struct File
        {
            std::vector<uint8_t> bytes;
            size_t position = 0;
        };

    public:
        Rml::FileHandle Open(const Rml::String& path) override
        {
            if (g_host.read_file == nullptr)
                return 0;
            auto file = std::make_unique<File>();
            Sink sink{ &file->bytes };
            if (g_host.read_file(g_host.user, path.c_str(), &sink) == 0)
                return 0;
            return reinterpret_cast<Rml::FileHandle>(file.release());
        }

        void Close(Rml::FileHandle file) override { delete reinterpret_cast<File*>(file); }

        size_t Read(void* buffer, size_t size, Rml::FileHandle handle) override
        {
            File* file = reinterpret_cast<File*>(handle);
            size_t count = std::min(size, file->bytes.size() - file->position);
            if (count > 0)
                std::memcpy(buffer, file->bytes.data() + file->position, count);
            file->position += count;
            return count;
        }

        bool Seek(Rml::FileHandle handle, long offset, int origin) override
        {
            File* file = reinterpret_cast<File*>(handle);
            long base = origin == SEEK_CUR ? (long)file->position : origin == SEEK_END ? (long)file->bytes.size() : 0;
            long target = base + offset;
            if (target < 0 || (size_t)target > file->bytes.size())
                return false;
            file->position = (size_t)target;
            return true;
        }

        size_t Tell(Rml::FileHandle handle) override { return reinterpret_cast<File*>(handle)->position; }

        size_t Length(Rml::FileHandle handle) override { return reinterpret_cast<File*>(handle)->bytes.size(); }
    };

    HostRenderInterface g_render;
    HostSystemInterface g_system;
    HostFileInterface g_files;

    // Fonts are read from memory that has to stay until after Rml::Shutdown.
    std::vector<std::unique_ptr<std::vector<uint8_t>>> g_fonts;

    // ---- Element handles -------------------------------------------------------------------------------------------------------------

    // A slot watches an element (an observer pointer becomes null when the element is destroyed) and owns it while no tree does. A handle is the slot's index plus one in
    // the low half and its generation in the high half, so a released slot that is used again does not answer to the old handle.
    struct Slot
    {
        Rml::ObserverPtr<Rml::Element> observer;
        Rml::ElementPtr owned;
        uint32_t generation = 1;
        bool used = false;
    };

    std::vector<Slot> g_slots;
    std::vector<uint32_t> g_freeSlots;

    ArElement Allocate(Rml::Element* element, Rml::ElementPtr owned = nullptr)
    {
        if (element == nullptr)
            return 0;
        uint32_t index;
        if (!g_freeSlots.empty())
        {
            index = g_freeSlots.back();
            g_freeSlots.pop_back();
        }
        else
        {
            index = (uint32_t)g_slots.size();
            g_slots.emplace_back();
        }
        Slot& slot = g_slots[index];
        slot.observer = element->GetObserverPtr();
        slot.owned = std::move(owned);
        slot.used = true;
        return ((uint64_t)slot.generation << 32) | (uint64_t)(index + 1);
    }

    Slot* SlotOf(ArElement handle)
    {
        uint32_t index = (uint32_t)(handle & 0xFFFFFFFFu);
        if (index == 0 || index > g_slots.size())
            return nullptr;
        Slot& slot = g_slots[index - 1];
        return slot.used && slot.generation == (uint32_t)(handle >> 32) ? &slot : nullptr;
    }

    Rml::Element* Get(ArElement handle)
    {
        Slot* slot = SlotOf(handle);
        return slot != nullptr ? slot->observer.get() : nullptr;
    }

    void FreeSlot(Slot& slot)
    {
        uint32_t index = (uint32_t)(&slot - g_slots.data());
        slot.owned.reset();
        slot.observer.reset();
        slot.used = false;
        slot.generation++;
        if (slot.generation == 0)
            slot.generation = 1;
        g_freeSlots.push_back(index);
    }

    void FreeAllSlots()
    {
        for (Slot& slot : g_slots)
        {
            slot.owned.reset();
            slot.observer.reset();
        }
        g_slots.clear();
        g_freeSlots.clear();
    }

    int32_t CopyOut(const Rml::String& text, char* buffer, int32_t capacity)
    {
        if (buffer != nullptr && capacity > 0)
        {
            size_t count = std::min(text.size(), (size_t)(capacity - 1));
            std::memcpy(buffer, text.data(), count);
            buffer[count] = 0;
        }
        return (int32_t)text.size();
    }

    // ---- Events ----------------------------------------------------------------------------------------------------------------------

    struct Listener;
    std::unordered_map<uint64_t, std::unique_ptr<Listener>> g_listeners;
    uint64_t g_nextToken = 1;

    struct Listener final : public Rml::EventListener
    {
        uint64_t token;
        Rml::String type;
        bool capture;
        Rml::ObserverPtr<Rml::Element> element;
        // The code of an on* attribute that the host has not compiled yet (see ArEventListenerInstancer).
        Rml::String pendingCode;
        bool pending = false;

        void ProcessEvent(Rml::Event& event) override;

        // The element let go of us (it was destroyed, or we were removed): this is the one place a listener dies.
        void OnDetach(Rml::Element*) override { g_listeners.erase(token); }
    };

    uint64_t DocumentId(Rml::ElementDocument* document) { return (uint64_t)(uintptr_t)document; }

    void Listener::ProcessEvent(Rml::Event& event)
    {
        if (pending)
        {
            // The attribute is set while the element is being parsed, before it is in the document: the host compiles the code now, with the document it is in.
            pending = false;
            Rml::Element* current = event.GetCurrentElement();
            Rml::ElementDocument* document = current != nullptr ? current->GetOwnerDocument() : nullptr;
            if (document == nullptr || g_host.compile_inline_handler == nullptr
                || g_host.compile_inline_handler(g_host.user, DocumentId(document), Allocate(document), token, pendingCode.c_str()) == 0)
                return;
        }
        if (g_host.on_event == nullptr)
            return;
        ArEvent wrapper{ &event };
        g_host.on_event(g_host.user, token, &wrapper);
    }

    // ---- Scripts in documents --------------------------------------------------------------------------------------------------------

    // The host's base stylesheet, put under the rules of every document loaded through this API (RmlUi has none of its own).
    Rml::SharedPtr<Rml::StyleSheetContainer> g_baseSheet;
    bool g_loadingDocument = false;

    // Every document is one of these, so that its <script>s reach the host and the host hears when it is gone.
    class ArDocument final : public Rml::ElementDocument
    {
    public:
        explicit ArDocument(const Rml::String& tag) : Rml::ElementDocument(tag), wantsBase(g_loadingDocument) {}

        // Under the document's own rules. Done before its body is made (the head has been read by then), so that text made while the body is read already has its font.
        void ApplyBase()
        {
            if (!wantsBase || baseApplied || !g_baseSheet)
                return;
            baseApplied = true;
            const Rml::StyleSheetContainer* current = GetStyleSheetContainer();
            SetStyleSheetContainer(current == nullptr ? g_baseSheet : g_baseSheet->CombineStyleSheetContainer(*current));
        }

        void OnChildAdd(Rml::Element* child) override
        {
            ApplyBase();
            Rml::ElementDocument::OnChildAdd(child);
        }

        ~ArDocument() override
        {
            if (g_host.document_closed != nullptr)
                g_host.document_closed(g_host.user, DocumentId(this));
        }

        void LoadInlineScript(const Rml::String& content, const Rml::String& source, int line) override
        {
            if (g_host.load_inline_script != nullptr)
                g_host.load_inline_script(g_host.user, DocumentId(this), Allocate(this), source.c_str(), content.c_str(), (int32_t)line);
        }

        void LoadExternalScript(const Rml::String& path) override
        {
            if (g_host.load_external_script != nullptr)
                g_host.load_external_script(g_host.user, DocumentId(this), Allocate(this), path.c_str());
        }

    private:
        bool wantsBase;
        bool baseApplied = false;
    };

    Rml::ElementInstancerGeneric<ArDocument> g_documentInstancer;

    // onclick="..." and the other on* attributes: the host compiles the code when the first event comes (the element is not in its document yet while its attributes are read), and its events
    // come back through on_event like any listener's.
    class ArEventListenerInstancer final : public Rml::EventListenerInstancer
    {
    public:
        Rml::EventListener* InstanceEventListener(const Rml::String& value, Rml::Element* element) override
        {
            if (g_host.compile_inline_handler == nullptr || element == nullptr)
                return nullptr;
            auto listener = std::make_unique<Listener>();
            listener->token = g_nextToken++;
            listener->capture = false;
            listener->element = element->GetObserverPtr();
            listener->pendingCode = value;
            listener->pending = true;
            if (g_host.instance_inline_handler != nullptr)
                g_host.instance_inline_handler(g_host.user, listener->token);
            Listener* raw = listener.get();
            g_listeners[raw->token] = std::move(listener);
            return raw;
        }
    };

    ArEventListenerInstancer g_listenerInstancer;

    // ---- Data models -----------------------------------------------------------------------------------------------------------------

    // The host holds the values; RmlUi's variables are refs the host gave out, and one definition per kind turns RmlUi's questions into host calls.
    void ToVariant(const ArVariant& from, Rml::Variant& to)
    {
        switch (from.type)
        {
        case AR_VARIANT_BOOL: to = from.integer != 0; break;
        case AR_VARIANT_INT: to = (int64_t)from.integer; break;
        case AR_VARIANT_DOUBLE: to = from.number; break;
        case AR_VARIANT_STRING: to = Rml::String(from.text != nullptr ? from.text : ""); break;
        default: to = Rml::String(); break;
        }
    }

    void FromVariant(const Rml::Variant& from, ArVariant& to, Rml::String& text)
    {
        to = {};
        switch (from.GetType())
        {
        case Rml::Variant::NONE: break;
        case Rml::Variant::BOOL: to.type = AR_VARIANT_BOOL; to.integer = from.Get<bool>() ? 1 : 0; break;
        case Rml::Variant::BYTE:
        case Rml::Variant::CHAR:
        case Rml::Variant::INT:
        case Rml::Variant::INT64:
        case Rml::Variant::UINT:
        case Rml::Variant::UINT64:
        {
            int64_t value = 0;
            from.GetInto(value);
            to.type = AR_VARIANT_INT;
            to.integer = value;
            break;
        }
        case Rml::Variant::FLOAT:
        case Rml::Variant::DOUBLE:
        {
            double value = 0;
            from.GetInto(value);
            to.type = AR_VARIANT_DOUBLE;
            to.number = value;
            break;
        }
        default:
            text = from.Get<Rml::String>();
            to.type = AR_VARIANT_STRING;
            to.text = text.c_str();
            break;
        }
    }

    class ArVariableDefinition final : public Rml::VariableDefinition
    {
    public:
        explicit ArVariableDefinition(Rml::DataVariableType type) : Rml::VariableDefinition(type) {}

        bool Get(void* ptr, Rml::Variant& variant) override
        {
            if (g_host.data_get == nullptr)
                return false;
            ArVariant value = {};
            if (g_host.data_get(g_host.user, (uint64_t)(uintptr_t)ptr, &value) == 0)
                return false;
            ToVariant(value, variant);
            return true;
        }

        bool Set(void* ptr, const Rml::Variant& variant) override
        {
            if (g_host.data_set == nullptr)
                return false;
            ArVariant value;
            Rml::String text;
            FromVariant(variant, value, text);
            return g_host.data_set(g_host.user, (uint64_t)(uintptr_t)ptr, &value) != 0;
        }

        int Size(void* ptr) override { return g_host.data_size != nullptr ? g_host.data_size(g_host.user, (uint64_t)(uintptr_t)ptr) : 0; }

        Rml::DataVariable Child(void* ptr, const Rml::DataAddressEntry& address) override;
    };

    ArVariableDefinition g_scalarDefinition(Rml::DataVariableType::Scalar);
    ArVariableDefinition g_arrayDefinition(Rml::DataVariableType::Array);
    ArVariableDefinition g_structDefinition(Rml::DataVariableType::Struct);

    Rml::DataVariable MakeVariable(uint64_t ref, int32_t kind)
    {
        switch (kind)
        {
        case AR_DATA_SCALAR: return Rml::DataVariable(&g_scalarDefinition, (void*)(uintptr_t)ref);
        case AR_DATA_ARRAY: return Rml::DataVariable(&g_arrayDefinition, (void*)(uintptr_t)ref);
        case AR_DATA_STRUCT: return Rml::DataVariable(&g_structDefinition, (void*)(uintptr_t)ref);
        default: return Rml::DataVariable();
        }
    }

    Rml::DataVariable ArVariableDefinition::Child(void* ptr, const Rml::DataAddressEntry& address)
    {
        if (g_host.data_child == nullptr)
            return Rml::DataVariable();
        int32_t kind = AR_DATA_MISSING;
        uint64_t child = g_host.data_child(g_host.user, (uint64_t)(uintptr_t)ptr, address.index < 0 ? address.name.c_str() : nullptr, address.index, &kind);
        return child != 0 ? MakeVariable(child, kind) : Rml::DataVariable();
    }
}


// ---- Exports ---------------------------------------------------------------------------------------------------------------------------

namespace
{
    Rml::Context* ContextOf(ArContext* context) { return reinterpret_cast<Rml::Context*>(context); }

    // Loads a document that gets the host's base stylesheet (also when its body is empty, which never asks for it).
    template <typename Load>
    Rml::ElementDocument* LoadWithBase(Load load)
    {
        g_loadingDocument = true;
        Rml::ElementDocument* document = load();
        g_loadingDocument = false;
        if (document != nullptr)
            if (auto* mine = dynamic_cast<ArDocument*>(document))
                mine->ApplyBase();
        return document;
    }

    Rml::ElementDocument* DocumentOf(ArElement handle)
    {
        Rml::Element* element = Get(handle);
        return element != nullptr ? rmlui_dynamic_cast<Rml::ElementDocument*>(element) : nullptr;
    }
}

extern "C"
{
    APOGEE_RMLUI_API void ar_sink_write(void* sink, const void* data, size_t size)
    {
        if (sink == nullptr || data == nullptr || size == 0)
            return;
        std::vector<uint8_t>* bytes = static_cast<Sink*>(sink)->bytes;
        const uint8_t* source = static_cast<const uint8_t*>(data);
        bytes->insert(bytes->end(), source, source + size);
    }

    APOGEE_RMLUI_API bool ar_initialize(const ArHost* host)
    {
        if (g_initialized)
            return false;
        g_host = {};
        if (host != nullptr)
            std::memcpy(&g_host, host, std::min<size_t>(host->size, sizeof(ArHost)));
        Rml::SetSystemInterface(&g_system);
        Rml::SetRenderInterface(&g_render);
        Rml::SetFileInterface(&g_files);
        g_initialized = Rml::Initialise();
        if (g_initialized)
        {
            // Documents and on* attributes reach the host's scripts.
            Rml::Factory::RegisterElementInstancer("body", &g_documentInstancer);
            Rml::Factory::RegisterEventListenerInstancer(&g_listenerInstancer);
        }
        return g_initialized;
    }

    APOGEE_RMLUI_API void ar_shutdown(void)
    {
        if (!g_initialized)
            return;
        // Listeners die with their elements (OnDetach); the slots hold no element alive that a context still owns.
        FreeAllSlots();
        g_baseSheet = nullptr;
        Rml::Shutdown();
        g_listeners.clear();
        g_fonts.clear();
        g_initialized = false;
        g_host = {};
    }

    APOGEE_RMLUI_API bool ar_set_base_style(const char* rcss)
    {
        if (!g_initialized)
            return false;
        g_baseSheet = rcss != nullptr && rcss[0] != 0 ? Rml::Factory::InstanceStyleSheetString(rcss) : nullptr;
        return rcss == nullptr || rcss[0] == 0 || (bool)g_baseSheet;
    }

    APOGEE_RMLUI_API const char* ar_version(void)
    {
        static Rml::String version;
        version = Rml::GetVersion();
        return version.c_str();
    }

    APOGEE_RMLUI_API bool ar_load_font(const uint8_t* data, size_t size, const char* family, bool italic, bool bold, bool fallback)
    {
        if (!g_initialized || data == nullptr || size == 0)
            return false;
        g_fonts.push_back(std::make_unique<std::vector<uint8_t>>(data, data + size));
        const std::vector<uint8_t>& copy = *g_fonts.back();
        bool loaded = Rml::LoadFontFace(Rml::Span<const Rml::byte>(copy.data(), copy.size()), family != nullptr ? family : "",
            italic ? Rml::Style::FontStyle::Italic : Rml::Style::FontStyle::Normal, bold ? Rml::Style::FontWeight::Bold : Rml::Style::FontWeight::Auto, fallback);
        if (!loaded)
            g_fonts.pop_back();
        return loaded;
    }

    // ---- Contexts ----

    APOGEE_RMLUI_API ArContext* ar_context_create(const char* name, int32_t width, int32_t height)
    {
        if (!g_initialized || name == nullptr)
            return nullptr;
        return reinterpret_cast<ArContext*>(Rml::CreateContext(name, Rml::Vector2i(width, height)));
    }

    APOGEE_RMLUI_API void ar_context_destroy(ArContext* context)
    {
        if (context != nullptr)
            Rml::RemoveContext(ContextOf(context)->GetName());
    }

    APOGEE_RMLUI_API void ar_context_set_size(ArContext* context, int32_t width, int32_t height) { ContextOf(context)->SetDimensions(Rml::Vector2i(width, height)); }

    APOGEE_RMLUI_API void ar_context_set_density(ArContext* context, float pixelsPerDp) { ContextOf(context)->SetDensityIndependentPixelRatio(pixelsPerDp); }

    APOGEE_RMLUI_API bool ar_context_update(ArContext* context) { return ContextOf(context)->Update(); }

    APOGEE_RMLUI_API bool ar_context_render(ArContext* context) { return ContextOf(context)->Render(); }

    APOGEE_RMLUI_API ArElement ar_context_load_document_text(ArContext* context, const char* rml, const char* sourceUrl)
    {
        if (context == nullptr || rml == nullptr)
            return 0;
        return Allocate(LoadWithBase([&] { return ContextOf(context)->LoadDocumentFromMemory(rml, sourceUrl != nullptr ? sourceUrl : "[document from memory]"); }));
    }

    APOGEE_RMLUI_API ArElement ar_context_load_document(ArContext* context, const char* path)
    {
        if (context == nullptr || path == nullptr)
            return 0;
        return Allocate(LoadWithBase([&] { return ContextOf(context)->LoadDocument(path); }));
    }

    APOGEE_RMLUI_API void ar_context_unload_document(ArContext* context, ArElement document)
    {
        if (Rml::ElementDocument* doc = DocumentOf(document); doc != nullptr && context != nullptr)
            ContextOf(context)->UnloadDocument(doc);
    }

    APOGEE_RMLUI_API void ar_document_show(ArElement document)
    {
        if (Rml::ElementDocument* doc = DocumentOf(document))
            doc->Show();
    }

    APOGEE_RMLUI_API void ar_document_hide(ArElement document)
    {
        if (Rml::ElementDocument* doc = DocumentOf(document))
            doc->Hide();
    }

    APOGEE_RMLUI_API ArElement ar_element_document(ArElement element)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? Allocate(e->GetOwnerDocument()) : 0;
    }

    APOGEE_RMLUI_API bool ar_document_add_style(ArElement document, const char* rcss, bool underneath)
    {
        Rml::ElementDocument* doc = DocumentOf(document);
        if (doc == nullptr || rcss == nullptr)
            return false;
        Rml::SharedPtr<Rml::StyleSheetContainer> sheet = Rml::Factory::InstanceStyleSheetString(rcss);
        if (!sheet)
            return false;
        const Rml::StyleSheetContainer* current = doc->GetStyleSheetContainer();
        if (current == nullptr)
            doc->SetStyleSheetContainer(sheet);
        else
            doc->SetStyleSheetContainer(underneath ? sheet->CombineStyleSheetContainer(*current) : current->CombineStyleSheetContainer(*sheet));
        return true;
    }

    APOGEE_RMLUI_API ArElement ar_document_create_element(ArElement document, const char* tag)
    {
        Rml::ElementDocument* doc = DocumentOf(document);
        if (doc == nullptr || tag == nullptr)
            return 0;
        Rml::ElementPtr element = doc->CreateElement(tag);
        Rml::Element* raw = element.get();
        return Allocate(raw, std::move(element));
    }

    APOGEE_RMLUI_API ArElement ar_document_create_text(ArElement document, const char* text)
    {
        Rml::ElementDocument* doc = DocumentOf(document);
        if (doc == nullptr || text == nullptr)
            return 0;
        Rml::ElementPtr element = doc->CreateTextNode(text);
        Rml::Element* raw = element.get();
        return Allocate(raw, std::move(element));
    }

    APOGEE_RMLUI_API ArElement ar_context_element_at(ArContext* context, float x, float y)
    {
        return context != nullptr ? Allocate(ContextOf(context)->GetElementAtPoint(Rml::Vector2f(x, y))) : 0;
    }

    APOGEE_RMLUI_API bool ar_context_is_mouse_over_ui(ArContext* context) { return context != nullptr && ContextOf(context)->IsMouseInteracting(); }

    APOGEE_RMLUI_API ArElement ar_context_focused_element(ArContext* context) { return context != nullptr ? Allocate(ContextOf(context)->GetFocusElement()) : 0; }

    // ---- Input ----

    APOGEE_RMLUI_API bool ar_context_mouse_move(ArContext* context, int32_t x, int32_t y, int32_t modifiers) { return ContextOf(context)->ProcessMouseMove(x, y, modifiers); }

    APOGEE_RMLUI_API bool ar_context_mouse_button(ArContext* context, int32_t button, bool down, int32_t modifiers)
    {
        return down ? ContextOf(context)->ProcessMouseButtonDown(button, modifiers) : ContextOf(context)->ProcessMouseButtonUp(button, modifiers);
    }

    APOGEE_RMLUI_API bool ar_context_mouse_wheel(ArContext* context, float x, float y, int32_t modifiers) { return ContextOf(context)->ProcessMouseWheel(Rml::Vector2f(x, y), modifiers); }

    APOGEE_RMLUI_API bool ar_context_mouse_leave(ArContext* context) { return ContextOf(context)->ProcessMouseLeave(); }

    APOGEE_RMLUI_API bool ar_context_key(ArContext* context, int32_t key, bool down, int32_t modifiers)
    {
        auto identifier = (Rml::Input::KeyIdentifier)key;
        return down ? ContextOf(context)->ProcessKeyDown(identifier, modifiers) : ContextOf(context)->ProcessKeyUp(identifier, modifiers);
    }

    APOGEE_RMLUI_API bool ar_context_text(ArContext* context, const char* text) { return text != nullptr && ContextOf(context)->ProcessTextInput(Rml::String(text)); }

    // ---- Elements ----

    APOGEE_RMLUI_API void ar_element_release(ArElement element)
    {
        if (Slot* slot = SlotOf(element))
            FreeSlot(*slot);
    }

    APOGEE_RMLUI_API bool ar_element_is_alive(ArElement element) { return Get(element) != nullptr; }

    APOGEE_RMLUI_API bool ar_element_append_child(ArElement parent, ArElement child)
    {
        Rml::Element* parentElement = Get(parent);
        Slot* childSlot = SlotOf(child);
        if (parentElement == nullptr || childSlot == nullptr || !childSlot->owned || childSlot->observer.get() == nullptr)
            return false;
        parentElement->AppendChild(std::move(childSlot->owned));
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_insert_before(ArElement parent, ArElement child, ArElement before)
    {
        Rml::Element* parentElement = Get(parent);
        Slot* childSlot = SlotOf(child);
        if (parentElement == nullptr || childSlot == nullptr || !childSlot->owned || childSlot->observer.get() == nullptr)
            return false;
        Rml::Element* anchor = before != 0 ? Get(before) : nullptr;
        if (before != 0 && (anchor == nullptr || anchor->GetParentNode() != parentElement))
            return false;
        if (anchor == nullptr)
            parentElement->AppendChild(std::move(childSlot->owned));
        else
            parentElement->InsertBefore(std::move(childSlot->owned), anchor);
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_remove_child(ArElement parent, ArElement child)
    {
        Rml::Element* parentElement = Get(parent);
        Slot* childSlot = SlotOf(child);
        Rml::Element* childElement = childSlot != nullptr ? childSlot->observer.get() : nullptr;
        if (parentElement == nullptr || childElement == nullptr || childElement->GetParentNode() != parentElement)
            return false;
        Rml::ElementPtr taken = parentElement->RemoveChild(childElement);
        if (!taken)
            return false;
        childSlot->owned = std::move(taken);
        return true;
    }

    APOGEE_RMLUI_API void ar_element_destroy(ArElement handle)
    {
        Slot* slot = SlotOf(handle);
        Rml::Element* element = slot != nullptr ? slot->observer.get() : nullptr;
        if (element == nullptr)
            return;
        if (slot->owned)
        {
            slot->owned.reset();
            return;
        }
        if (Rml::Element* parent = element->GetParentNode())
        {
            Rml::ElementPtr taken = parent->RemoveChild(element);
            taken.reset();
        }
    }

    APOGEE_RMLUI_API ArElement ar_element_parent(ArElement element)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? Allocate(e->GetParentNode()) : 0;
    }

    APOGEE_RMLUI_API int32_t ar_element_child_count(ArElement element)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? e->GetNumChildren() : -1;
    }

    APOGEE_RMLUI_API ArElement ar_element_child(ArElement element, int32_t index)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && index >= 0 && index < e->GetNumChildren() ? Allocate(e->GetChild(index)) : 0;
    }

    APOGEE_RMLUI_API ArElement ar_element_by_id(ArElement root, const char* id)
    {
        Rml::Element* e = Get(root);
        return e != nullptr && id != nullptr ? Allocate(e->GetElementById(id)) : 0;
    }

    APOGEE_RMLUI_API ArElement ar_element_closest(ArElement element, const char* selector)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && selector != nullptr ? Allocate(e->Closest(selector)) : 0;
    }

    APOGEE_RMLUI_API bool ar_element_contains(ArElement outer, ArElement inner)
    {
        Rml::Element* o = Get(outer);
        Rml::Element* i = Get(inner);
        return o != nullptr && i != nullptr && o->Contains(i);
    }

    APOGEE_RMLUI_API int32_t ar_element_tag(ArElement element, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? CopyOut(e->GetTagName(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API int32_t ar_element_get_id(ArElement element, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? CopyOut(e->GetId(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API bool ar_element_set_id(ArElement element, const char* id)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || id == nullptr)
            return false;
        e->SetId(id);
        return true;
    }

    APOGEE_RMLUI_API int32_t ar_element_get_class_names(ArElement element, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? CopyOut(e->GetClassNames(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API bool ar_element_set_class_names(ArElement element, const char* classNames)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || classNames == nullptr)
            return false;
        e->SetClassNames(classNames);
        return true;
    }

    APOGEE_RMLUI_API int32_t ar_element_get_attribute(ArElement element, const char* name, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || name == nullptr)
            return -1;
        Rml::Variant* value = e->GetAttribute(name);
        return value != nullptr ? CopyOut(value->Get<Rml::String>(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API bool ar_element_set_attribute(ArElement element, const char* name, const char* value)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || name == nullptr || value == nullptr)
            return false;
        e->SetAttribute(name, Rml::String(value));
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_remove_attribute(ArElement element, const char* name)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || name == nullptr)
            return false;
        e->RemoveAttribute(name);
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_has_attribute(ArElement element, const char* name)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && name != nullptr && e->GetAttribute(name) != nullptr;
    }

    APOGEE_RMLUI_API bool ar_element_set_property(ArElement element, const char* name, const char* value)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && name != nullptr && value != nullptr && e->SetProperty(name, value);
    }

    APOGEE_RMLUI_API bool ar_element_remove_property(ArElement element, const char* name)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || name == nullptr)
            return false;
        e->RemoveProperty(name);
        return true;
    }

    APOGEE_RMLUI_API int32_t ar_element_get_inner_rml(ArElement element, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(element);
        return e != nullptr ? CopyOut(e->GetInnerRML(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API bool ar_element_set_inner_rml(ArElement element, const char* rml)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || rml == nullptr)
            return false;
        e->SetInnerRML(rml);
        return true;
    }

    APOGEE_RMLUI_API bool ar_text_set(ArElement text, const char* value)
    {
        Rml::Element* e = Get(text);
        auto* node = e != nullptr ? rmlui_dynamic_cast<Rml::ElementText*>(e) : nullptr;
        if (node == nullptr || value == nullptr)
            return false;
        node->SetText(value);
        return true;
    }

    APOGEE_RMLUI_API int32_t ar_text_get(ArElement text, char* buffer, int32_t capacity)
    {
        Rml::Element* e = Get(text);
        auto* node = e != nullptr ? rmlui_dynamic_cast<Rml::ElementText*>(e) : nullptr;
        return node != nullptr ? CopyOut(node->GetText(), buffer, capacity) : -1;
    }

    APOGEE_RMLUI_API bool ar_element_set_pseudo_class(ArElement element, const char* name, bool on)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || name == nullptr)
            return false;
        e->SetPseudoClass(name, on);
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_is_pseudo_class_set(ArElement element, const char* name)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && name != nullptr && e->IsPseudoClassSet(name);
    }

    APOGEE_RMLUI_API bool ar_element_focus(ArElement element)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && e->Focus();
    }

    APOGEE_RMLUI_API void ar_element_blur(ArElement element)
    {
        if (Rml::Element* e = Get(element))
            e->Blur();
    }

    APOGEE_RMLUI_API void ar_element_click(ArElement element)
    {
        if (Rml::Element* e = Get(element))
            e->Click();
    }

    APOGEE_RMLUI_API void ar_element_scroll_into_view(ArElement element, bool alignWithTop)
    {
        if (Rml::Element* e = Get(element))
            e->ScrollIntoView(alignWithTop);
    }

    APOGEE_RMLUI_API bool ar_element_get_rect(ArElement element, ArRect* rect)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || rect == nullptr)
            return false;
        Rml::Vector2f offset = e->GetAbsoluteOffset(Rml::BoxArea::Border);
        Rml::Vector2f size = e->GetBox().GetSize(Rml::BoxArea::Border);
        *rect = ArRect{ offset.x, offset.y, size.x, size.y };
        return true;
    }

    APOGEE_RMLUI_API bool ar_element_is_visible(ArElement element)
    {
        Rml::Element* e = Get(element);
        return e != nullptr && e->IsVisible(true);
    }

    // ---- Events ----

    APOGEE_RMLUI_API uint64_t ar_element_add_listener(ArElement element, const char* type, bool capture)
    {
        Rml::Element* e = Get(element);
        if (e == nullptr || type == nullptr)
            return 0;
        auto listener = std::make_unique<Listener>();
        listener->token = g_nextToken++;
        listener->type = type;
        listener->capture = capture;
        listener->element = e->GetObserverPtr();
        Listener* raw = listener.get();
        g_listeners[raw->token] = std::move(listener);
        e->AddEventListener(raw->type, raw, capture);
        return raw->token;
    }

    APOGEE_RMLUI_API bool ar_element_remove_listener(uint64_t token)
    {
        auto found = g_listeners.find(token);
        if (found == g_listeners.end())
            return false;
        Listener* listener = found->second.get();
        if (Rml::Element* e = listener->element.get())
            e->RemoveEventListener(listener->type, listener, listener->capture); // calls OnDetach, which erases (and so deletes) it
        else
            g_listeners.erase(found);
        return true;
    }

    APOGEE_RMLUI_API bool ar_listener_exists(uint64_t token) { return g_listeners.find(token) != g_listeners.end(); }

    APOGEE_RMLUI_API int32_t ar_event_type(const ArEvent* event, char* buffer, int32_t capacity) { return CopyOut(event->event->GetType(), buffer, capacity); }

    APOGEE_RMLUI_API ArElement ar_event_target(ArEvent* event) { return Allocate(event->event->GetTargetElement()); }

    APOGEE_RMLUI_API ArElement ar_event_current(ArEvent* event) { return Allocate(event->event->GetCurrentElement()); }

    APOGEE_RMLUI_API int32_t ar_event_phase(const ArEvent* event) { return (int32_t)event->event->GetPhase(); }

    APOGEE_RMLUI_API void ar_event_stop_propagation(ArEvent* event) { event->event->StopPropagation(); }

    APOGEE_RMLUI_API bool ar_event_has_parameter(const ArEvent* event, const char* key)
    {
        return key != nullptr && event->event->GetParameters().find(key) != event->event->GetParameters().end();
    }

    APOGEE_RMLUI_API int32_t ar_event_get_string(const ArEvent* event, const char* key, char* buffer, int32_t capacity)
    {
        if (key == nullptr)
            return -1;
        auto found = event->event->GetParameters().find(key);
        if (found == event->event->GetParameters().end())
            return -1;
        return CopyOut(found->second.Get<Rml::String>(), buffer, capacity);
    }

    APOGEE_RMLUI_API double ar_event_get_number(const ArEvent* event, const char* key, double fallback)
    {
        if (key == nullptr)
            return fallback;
        auto found = event->event->GetParameters().find(key);
        return found != event->event->GetParameters().end() ? found->second.Get<double>(fallback) : fallback;
    }

    // ---- Data models ----

    APOGEE_RMLUI_API bool ar_data_model_create(ArContext* context, const char* model)
    {
        if (context == nullptr || model == nullptr || ContextOf(context)->GetDataModels().count(model) != 0)
            return false;
        Rml::DataModelConstructor constructor = ContextOf(context)->CreateDataModel(model);
        return (bool)constructor;
    }

    APOGEE_RMLUI_API bool ar_data_model_bind(ArContext* context, const char* model, const char* name, uint64_t ref, int32_t kind)
    {
        if (context == nullptr || model == nullptr || name == nullptr || ref == 0)
            return false;
        Rml::DataModelConstructor constructor = ContextOf(context)->GetDataModel(model);
        Rml::DataVariable variable = MakeVariable(ref, kind);
        return constructor && variable && constructor.BindCustomDataVariable(name, variable);
    }

    APOGEE_RMLUI_API bool ar_data_model_bind_event(ArContext* context, const char* model, const char* name, uint64_t token)
    {
        if (context == nullptr || model == nullptr || name == nullptr)
            return false;
        Rml::DataModelConstructor constructor = ContextOf(context)->GetDataModel(model);
        if (!constructor)
            return false;
        return constructor.BindEventCallback(name, [token](Rml::DataModelHandle, Rml::Event& event, const Rml::VariantList& arguments) {
            if (g_host.data_event == nullptr)
                return;
            std::vector<ArVariant> values(arguments.size());
            std::vector<Rml::String> texts(arguments.size());
            for (size_t i = 0; i < arguments.size(); i++)
                FromVariant(arguments[i], values[i], texts[i]);
            ArEvent wrapper{ &event };
            g_host.data_event(g_host.user, token, &wrapper, values.data(), (int32_t)values.size());
        });
    }

    APOGEE_RMLUI_API bool ar_data_model_dirty(ArContext* context, const char* model, const char* name)
    {
        if (context == nullptr || model == nullptr)
            return false;
        Rml::DataModelConstructor constructor = ContextOf(context)->GetDataModel(model);
        if (!constructor)
            return false;
        Rml::DataModelHandle handle = constructor.GetModelHandle();
        if (name != nullptr)
            handle.DirtyVariable(name);
        else
            handle.DirtyAllVariables();
        return true;
    }

    APOGEE_RMLUI_API bool ar_data_model_is_dirty(ArContext* context, const char* model, const char* name)
    {
        if (context == nullptr || model == nullptr || name == nullptr)
            return false;
        Rml::DataModelConstructor constructor = ContextOf(context)->GetDataModel(model);
        if (!constructor)
            return false;
        Rml::DataModelHandle handle = constructor.GetModelHandle();
        return handle.IsVariableDirty(name);
    }

    APOGEE_RMLUI_API bool ar_data_model_remove(ArContext* context, const char* model)
    {
        return context != nullptr && model != nullptr && ContextOf(context)->RemoveDataModel(model);
    }

    // ---- The debugger ----

    APOGEE_RMLUI_API bool ar_debugger_initialize(ArContext* context) { return context != nullptr && Rml::Debugger::Initialise(ContextOf(context)); }

    APOGEE_RMLUI_API void ar_debugger_set_visible(bool visible) { Rml::Debugger::SetVisible(visible); }

    APOGEE_RMLUI_API bool ar_debugger_is_visible(void) { return Rml::Debugger::IsVisible(); }
}
