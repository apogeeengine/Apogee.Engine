/*
 * FreeType's configuration for Apogee: the options of upstream's ftoption.h, then the few we change. FT_CONFIG_OPTIONS_H names this file
 * for the library and for everything that includes FreeType's headers (cimgui, the bindings generator), so that they all agree.
 *
 * Left out, with the modules and libraries behind them (see ThirdParty/README.md): LZW (.Z) compressed fonts and the SVG glyph hooks, because
 * we do not build those modules. zlib stays (FreeType's own copy, for WOFF fonts and .gz files); bzip2, brotli (WOFF2), libpng (colour
 * PNG glyphs) and HarfBuzz are off in upstream's defaults and stay off.
 */
#ifndef APOGEE_FTOPTION_H_
#define APOGEE_FTOPTION_H_

#include <freetype/config/ftoption.h>

#undef FT_CONFIG_OPTION_USE_LZW
#undef FT_CONFIG_OPTION_SVG

#endif
