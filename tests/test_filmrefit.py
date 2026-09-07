import importlib.util
import tempfile
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

    def test_half_step_sony_timecode_matches_ffprobe_double_frame_number(self):
        filmrefit = load_filmrefit_module()

        self.assertTrue(
            filmrefit.timecodes_equivalent(
                "09:50:58:38",
                "09:50:58;19",
                "true",
            )
        )

    def test_half_step_sony_timecode_expands_to_full_rate_frame_number(self):
        filmrefit = load_filmrefit_module()

        self.assertEqual(
            "09:50:58;38",
            filmrefit.expand_half_step_timecode("09:50:58;19"),
        )

    def test_half_step_sony_timecode_detects_real_mismatch(self):
        filmrefit = load_filmrefit_module()

        self.assertFalse(
            filmrefit.timecodes_equivalent(
                "09:50:58:40",
                "09:50:58;19",
                "true",
            )
        )

    def test_failed_output_path_adds_failed_suffix_after_output_kind(self):
        filmrefit = load_filmrefit_module()

        self.assertEqual(
            Path("C2804_PROXY_FAILED.mov"),
            filmrefit.build_failed_output_path(Path("C2804_PROXY.mov")),
        )
        self.assertEqual(
            Path("C2804_MEZZANINE_FAILED.mov"),
            filmrefit.build_failed_output_path(Path("C2804_MEZZANINE.mov")),
        )

    def test_failed_output_path_avoids_existing_failed_file(self):
        filmrefit = load_filmrefit_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            output_path = Path(temp_dir) / "C2804_PROXY.mov"
            (Path(temp_dir) / "C2804_PROXY_FAILED.mov").touch()

            self.assertEqual(
                Path(temp_dir) / "C2804_PROXY_FAILED_1.mov",
                filmrefit.build_failed_output_path(output_path),
            )

    def test_timecode_repair_command_stream_copies_with_timecode(self):
        filmrefit = load_filmrefit_module()

        command = filmrefit.build_timecode_repair_command(
            input_path=Path("C2805_PROXY.mov"),
            output_path=Path("C2805_PROXY_TCFIX.mov"),
            timecode="08:56:36;56",
        )

        self.assertIn("-c", command)
        self.assertIn("copy", command)
        self.assertIn("-timecode", command)
        self.assertIn("08:56:36;56", command)

    def test_resolve_source_for_proxy_output(self):
        filmrefit = load_filmrefit_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "C2805.MP4"
            proxy_path = Path(temp_dir) / "C2805_PROXY.mov"
            source_path.touch()
            proxy_path.touch()

            self.assertEqual(
                source_path,
                filmrefit.resolve_source_for_output(proxy_path),
            )

    def test_bad_timecode_output_path_adds_backup_suffix(self):
        filmrefit = load_filmrefit_module()

        self.assertEqual(
            Path("C2805_PROXY_BAD_TC.mov"),
            filmrefit.build_bad_timecode_output_path(Path("C2805_PROXY.mov")),
        )

    def test_format_file_size_uses_binary_units(self):
        filmrefit = load_filmrefit_module()

        self.assertEqual("1.5 KiB", filmrefit.format_file_size(1536))


if __name__ == "__main__":
    unittest.main()
