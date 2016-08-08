

CFLAGS += -I ../../skia/include/c/
CFLAGS += -I ../../skia/include/core/
CFLAGS += -I ../../skia/include/config/
CFLAGS += -I ../../skia/include/codec/
CFLAGS += -I ../../skia/include/effects/
CFLAGS += -I ../../skia/src/c/

CXXFLAGS = $(CFLAGS)

CFLAGS += -std=c++11 -fPIC

SKIA_ARCHIVES += ../../skia/out/Debug/libskia_core.a
SKIA_ARCHIVES += ../../skia/out/Debug/libpng_static.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_codec.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_codec_android.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_effects.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_images.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts_avx2.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts_avx.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts_sse41.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts_sse42.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_opts_ssse3.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_pdf.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_ports.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_sfnt.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_skgpu.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_skgputest.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_svg.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_views.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_utils.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_xml.a
SKIA_ARCHIVES += ../../skia/out/Debug/libskia_xps.a


libSkiaSharp.so: SkiaKeeper.o SkManagedStream.o sk_xamarin.o sk_managedstream.o

libSkiaSharp.so:
		g++ -shared -o $@ SkiaKeeper.o SkManagedStream.o sk_xamarin.o sk_managedstream.o ~/develop/ve/SkiaSharp/skia/out/Debug/lib/libskia.so -ljpeg

clean:
	rm -rf *.o
	rm -rf libSkiaSharp.so
