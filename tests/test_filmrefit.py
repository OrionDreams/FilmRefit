import importlib.util
import unittest
from pathlib import Path


def load_filmrefit_module():
    module_path = Path(__file__).resolve().parents[1] / "transcoder" / "filmrefit.py"
    spec = importlib.util.spec_from_file_location("filmrefit", module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FilmRefitCommandTests(unittest.TestCase):
    def test_build_ffmpeg_command_writes_quicktime_camera_metadata(self):
        filmrefit = load_filmrefit_module()

        command = filmrefit.build_ffmpeg_command(
            input_path=Path("DJI_0001.MP4"),
            output_path=Path("DJI_0001_PROXY.mov"),
            codec="h264",
            mode="proxy",
            timecode=None,
            camera="DJI Mini4 Pro",
        )

        self.assertIn("-movflags", command)
        self.assertIn("use_metadata_tags", command)
        self.assertIn("-metadata", command)
        self.assertIn("com.apple.quicktime.make=DJI Mini4 Pro", command)

    def test_build_ffmpeg_command_omits_camera_metadata_when_unknown(self):
        filmrefit = load_filmrefit_module()

        command = filmrefit.build_ffmpeg_command(
            input_path=Path("C0001.MP4"),
            output_path=Path("C0001_PROXY.mov"),
            codec="h264",
            mode="proxy",
            timecode=None,
        )

        self.assertNotIn("com.apple.quicktime.make=", command)


if __name__ == "__main__":
    unittest.main()
