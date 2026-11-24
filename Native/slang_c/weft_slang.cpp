#include "weft_slang.h"

#include <slang-com-ptr.h>
#include <slang-tag-version.h>
#include <slang.h>

#include <mutex>
#include <string>
#include <vector>

struct WeftSlangSession
{
    Slang::ComPtr<slang::ISession> session;
};

struct WeftSlangResult
{
    SlangResult status = SLANG_FAIL;
    std::string diagnostics;
    std::vector<std::string> entry_point_names;
    std::vector<Slang::ComPtr<slang::IBlob>> code;
    std::string reflection_json;
    std::vector<std::string> dependencies;
};

namespace
{
std::mutex g_global_lock;
// Loading the core module is the expensive part of Slang: every session of the process shares one global session.
Slang::ComPtr<slang::IGlobalSession> g_global;

slang::IGlobalSession* GlobalSession()
{
    std::lock_guard<std::mutex> lock(g_global_lock);
    if (!g_global)
    {
        SlangGlobalSessionDesc desc = {};
        slang_createGlobalSession2(&desc, g_global.writeRef());
    }

    return g_global;
}

void Append(std::string& to, slang::IBlob* diagnostics)
{
    if (diagnostics && diagnostics->getBufferSize() > 0)
    {
        to.append(static_cast<const char*>(diagnostics->getBufferPointer()), diagnostics->getBufferSize());
        if (to.back() != '\n')
        {
            to.push_back('\n');
        }
    }
}
}

extern "C"
{
const char* weft_slang_version(void)
{
    return SLANG_TAG_VERSION;
}

int32_t weft_slang_session_create(const WeftSlangSessionDesc* desc, WeftSlangSession** out_session)
{
    *out_session = nullptr;
    slang::IGlobalSession* global = GlobalSession();
    if (!global)
    {
        return SLANG_FAIL;
    }

    slang::TargetDesc target = {};
    if (desc->target == WEFT_SLANG_TARGET_METAL)
    {
        target.format = SLANG_METAL;
    }
    else
    {
        target.format = SLANG_SPIRV;
        target.profile = global->findProfile("spirv_1_6");
    }

    // Entry points keep their names in the SPIR-V (the default would call every one "main"), so a pipeline can find them.
    slang::CompilerOptionEntry options[4] = {};
    options[0].name = slang::CompilerOptionName::VulkanUseEntryPointName;
    options[0].value.intValue0 = 1;
    options[1].name = slang::CompilerOptionName::Optimization;
    options[1].value.intValue0 = desc->optimization;
    options[2].name = slang::CompilerOptionName::DebugInformation;
    options[2].value.intValue0 = desc->debug_info;
    // The engine's shaders pin Metal buffer indices with `register(bN)` (Metal has no descriptor sets); Slang warns that Vulkan has no matching binding, which a Metal build does not need.
    options[3].name = slang::CompilerOptionName::DisableWarning;
    options[3].value.kind = slang::CompilerOptionValueKind::String;
    options[3].value.stringValue0 = "39029";

    std::vector<slang::PreprocessorMacroDesc> macros;
    for (int32_t i = 0; i < desc->macro_count; i++)
    {
        macros.push_back({desc->macros[i].name, desc->macros[i].value});
    }

    slang::SessionDesc session_desc = {};
    session_desc.targets = &target;
    session_desc.targetCount = 1;
    session_desc.defaultMatrixLayoutMode = SLANG_MATRIX_LAYOUT_ROW_MAJOR;
    session_desc.searchPaths = desc->search_paths;
    session_desc.searchPathCount = desc->search_path_count;
    session_desc.preprocessorMacros = macros.data();
    session_desc.preprocessorMacroCount = static_cast<SlangInt>(macros.size());
    session_desc.compilerOptionEntries = options;
    session_desc.compilerOptionEntryCount = desc->target == WEFT_SLANG_TARGET_METAL ? 4 : 3;

    auto* session = new WeftSlangSession();
    SlangResult result = global->createSession(session_desc, session->session.writeRef());
    if (SLANG_FAILED(result))
    {
        delete session;
        return result;
    }

    *out_session = session;
    return result;
}

void weft_slang_session_destroy(WeftSlangSession* session)
{
    delete session;
}

int32_t weft_slang_compile(
    WeftSlangSession* session, const char* module_name, const char* path, const char* source,
    const char* const* entry_points, int32_t entry_point_count, WeftSlangResult** out_result)
{
    auto* result = new WeftSlangResult();
    *out_result = result;

    Slang::ComPtr<slang::IBlob> diagnostics;
    slang::IModule* module = source
        ? session->session->loadModuleFromSourceString(module_name, path, source, diagnostics.writeRef())
        : session->session->loadModule(module_name, diagnostics.writeRef());
    Append(result->diagnostics, diagnostics);
    if (!module)
    {
        return result->status = SLANG_FAIL;
    }

    for (int32_t i = 0; i < module->getDependencyFileCount(); i++)
    {
        result->dependencies.push_back(module->getDependencyFilePath(i));
    }

    // The program is the module plus its entry points.
    std::vector<Slang::ComPtr<slang::IEntryPoint>> found;
    if (entry_point_count > 0)
    {
        for (int32_t i = 0; i < entry_point_count; i++)
        {
            Slang::ComPtr<slang::IEntryPoint> entry;
            if (SLANG_FAILED(module->findEntryPointByName(entry_points[i], entry.writeRef())))
            {
                result->diagnostics += std::string("error: the module defines no entry point named '") + entry_points[i] + "'\n";
                return result->status = SLANG_E_NOT_FOUND;
            }

            found.push_back(entry);
        }
    }
    else
    {
        for (int32_t i = 0; i < module->getDefinedEntryPointCount(); i++)
        {
            Slang::ComPtr<slang::IEntryPoint> entry;
            if (SLANG_SUCCEEDED(module->getDefinedEntryPoint(i, entry.writeRef())))
            {
                found.push_back(entry);
            }
        }
    }

    std::vector<slang::IComponentType*> components = {module};
    for (auto& entry : found)
    {
        components.push_back(entry);
        result->entry_point_names.push_back(entry->getFunctionReflection()->getName());
    }

    Slang::ComPtr<slang::IComponentType> composite;
    diagnostics = nullptr;
    SlangResult status = session->session->createCompositeComponentType(
        components.data(), static_cast<SlangInt>(components.size()), composite.writeRef(), diagnostics.writeRef());
    Append(result->diagnostics, diagnostics);
    if (SLANG_FAILED(status))
    {
        return result->status = status;
    }

    Slang::ComPtr<slang::IComponentType> linked;
    diagnostics = nullptr;
    status = composite->link(linked.writeRef(), diagnostics.writeRef());
    Append(result->diagnostics, diagnostics);
    if (SLANG_FAILED(status))
    {
        return result->status = status;
    }

    for (size_t i = 0; i < found.size(); i++)
    {
        Slang::ComPtr<slang::IBlob> code;
        diagnostics = nullptr;
        status = linked->getEntryPointCode(static_cast<SlangInt>(i), 0, code.writeRef(), diagnostics.writeRef());
        Append(result->diagnostics, diagnostics);
        if (SLANG_FAILED(status) || !code)
        {
            return result->status = SLANG_FAILED(status) ? status : SLANG_FAIL;
        }

        result->code.push_back(code);
    }

    diagnostics = nullptr;
    if (slang::ProgramLayout* layout = linked->getLayout(0, diagnostics.writeRef()))
    {
        Slang::ComPtr<slang::IBlob> json;
        if (SLANG_SUCCEEDED(layout->toJson(json.writeRef())) && json)
        {
            result->reflection_json.assign(static_cast<const char*>(json->getBufferPointer()), json->getBufferSize());
        }
    }

    Append(result->diagnostics, diagnostics);
    return result->status = SLANG_OK;
}

int32_t weft_slang_result_status(const WeftSlangResult* result) { return result->status; }

const char* weft_slang_result_diagnostics(const WeftSlangResult* result) { return result->diagnostics.c_str(); }

int32_t weft_slang_result_entry_point_count(const WeftSlangResult* result) { return static_cast<int32_t>(result->code.size()); }

const char* weft_slang_result_entry_point_name(const WeftSlangResult* result, int32_t index)
{
    return result->entry_point_names[static_cast<size_t>(index)].c_str();
}

const void* weft_slang_result_code(const WeftSlangResult* result, int32_t index, size_t* out_size)
{
    slang::IBlob* blob = result->code[static_cast<size_t>(index)];
    *out_size = blob->getBufferSize();
    return blob->getBufferPointer();
}

const char* weft_slang_result_reflection_json(const WeftSlangResult* result) { return result->reflection_json.c_str(); }

int32_t weft_slang_result_dependency_count(const WeftSlangResult* result) { return static_cast<int32_t>(result->dependencies.size()); }

const char* weft_slang_result_dependency(const WeftSlangResult* result, int32_t index)
{
    return result->dependencies[static_cast<size_t>(index)].c_str();
}

void weft_slang_result_destroy(WeftSlangResult* result) { delete result; }
}
