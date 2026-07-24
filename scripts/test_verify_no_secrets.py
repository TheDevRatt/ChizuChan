from __future__ import annotations

import unittest

import verify_no_secrets as scanner


class CredentialScannerTests(unittest.TestCase):
    def test_service_specific_keys_are_sensitive(self) -> None:
        findings: list[str] = []
        scanner.inspect_json(
            {
                "ApiKeys": {
                    "OverseerrKey": "value",
                    "SonarrAnimeKey": "value",
                    "RadarrMovieKey": "value",
                },
                "Lidarr": {"ApiKey": "value"},
            },
            "secrets.json",
            findings,
        )

        self.assertEqual(4, len(findings))

    def test_public_and_environment_variable_names_are_safe(self) -> None:
        findings: list[str] = []
        scanner.inspect_json(
            {
                "Discord": {"PublicKey": "public-value"},
                "Provider": {"ApiKeyEnvironmentVariable": "GROQ_API_KEY"},
            },
            "appsettings.example.json",
            findings,
        )

        self.assertEqual([], findings)

    def test_configuration_names_are_case_insensitive(self) -> None:
        for name in (
            "appsettings.Production.JSON",
            "APPSETTINGS.json",
            "Secrets.JSON",
        ):
            with self.subTest(name=name):
                self.assertTrue(scanner.is_configuration_json(scanner.pathlib.Path(name)))

    def test_known_token_formats_are_detected(self) -> None:
        samples = {
            "Discord bot token": "M" + "a" * 23 + "." + "b" * 6 + "." + "c" * 24,
            "GitHub token": "gh" + "p_" + "a" * 24,
            "OpenAI-style key": "s" + "k-" + "a" * 24,
            "Groq key": "g" + "sk_" + "a" * 24,
            "OpenRouter key": "s" + "k-or-v1-" + "a" * 24,
        }

        for label, value in samples.items():
            with self.subTest(label=label):
                self.assertIsNotNone(scanner.TOKEN_PATTERNS[label].search(value))


if __name__ == "__main__":
    unittest.main()
