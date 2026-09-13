"""Keep all four tracked .NET acceptance projects in the CI test matrix."""
import pathlib
import unittest


class LongMediaCiTests(unittest.TestCase):
    def test_all_tracked_projects_are_in_security_workflow(self):
        root = pathlib.Path(__file__).resolve().parents[1]
        workflow = (root / '.github/workflows/security.yml').read_text()
        projects = sorted((root / 'tests').glob('*/*.csproj'))
        self.assertEqual(4, len(projects))
        for project in projects:
            with self.subTest(project=project.stem):
                self.assertIn(project.stem, workflow)


if __name__ == '__main__':
    unittest.main()
