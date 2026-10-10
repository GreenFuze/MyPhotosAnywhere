# MPA launcher identity

Two overlapping photo frames represent one library across storage sources. Mint mountains and an amber sun suggest photography; the orbit suggests access anywhere. Deep teal matches MPA's interface. The mark is original to MPA and does not use Google's Photos or Drive logos.

The production artwork is the Android vector in `app/src/main/res/drawable/ic_launcher_foreground.xml`. Its geometry fits the adaptive icon safe region. Separate background/foreground layers support launcher masks; Android 13+ also receives a matching monochrome layer for themed icons. Both the regular and round manifest icon references are configured. Minimum Android API 26 supports adaptive icons directly.

`mpa-icon.svg` is a scalable presentation copy with a rounded teal background. Production launcher masks determine the final shape; the app does not bake a rounded square into its adaptive foreground. The SVG was checked visually, and Android resource compilation/lint check the packaged assets.

The generated design concept informed the clean vector implementation. Gradients, raster edges and tiny details were removed from the production artwork to keep it crisp at launcher size. This adds no runtime image dependency.
