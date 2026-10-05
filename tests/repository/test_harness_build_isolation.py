"""Contract tests for the optional integration-harness compile set."""

import json
import os
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / 'HOOPER GAME.csproj'
INTEGRATION_ROOT = ROOT / 'tests' / 'integration'
CI_WORKFLOW = ROOT / '.github' / 'workflows' / 'ci.yml'


def evaluated_compile_items(include_harness: bool | None) -> set[Path]:
    command = ['dotnet', 'msbuild', str(PROJECT), '-nologo', '-getItem:Compile']
    if include_harness is not None:
        command.append(f'-p:IncludeIntegrationHarness={str(include_harness).lower()}')
    environment = os.environ.copy()
    environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    completed = subprocess.run(
        command,
        cwd=ROOT,
        env=environment,
        check=True,
        capture_output=True,
        text=True,
    )
    payload = json.loads(completed.stdout)
    return {Path(item['FullPath']).resolve() for item in payload['Items']['Compile']}


def integration_sources(items: set[Path]) -> set[Path]:
    return {
        item
        for item in items
        if item.is_relative_to(INTEGRATION_ROOT.resolve())
    }


class HarnessBuildIsolationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.expected = {path.resolve() for path in INTEGRATION_ROOT.rglob('*.cs')}

    def test_compatibility_default_includes_every_harness_source(self):
        self.assertEqual(self.expected, integration_sources(evaluated_compile_items(None)))

    def test_explicit_harness_mode_includes_every_harness_source(self):
        self.assertEqual(self.expected, integration_sources(evaluated_compile_items(True)))

    def test_ordinary_mode_excludes_every_harness_source(self):
        self.assertEqual(set(), integration_sources(evaluated_compile_items(False)))

    def test_misspelled_mode_fails_closed_with_a_clear_diagnostic(self):
        completed = subprocess.run(
            [
                'dotnet',
                'build',
                str(PROJECT),
                '--no-restore',
                '-nologo',
                '-p:IncludeIntegrationHarness=tru',
                f'-p:ProjectAssetsFile={ROOT / ".godot" / "missing-project.assets.json"}',
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
        output = completed.stdout + completed.stderr
        self.assertNotEqual(0, completed.returncode)
        self.assertIn('IncludeIntegrationHarness must be exactly true or false', output)

    def test_ci_builds_each_surface_with_an_explicit_mode(self):
        workflow = CI_WORKFLOW.read_text(encoding='utf-8')
        self.assertIn(
            'dotnet build "HOOPER GAME.csproj" --configuration Debug '
            '-p:IncludeIntegrationHarness=false',
            workflow,
        )
        self.assertEqual(
            2,
            workflow.count(
                'dotnet build "HOOPER GAME.csproj" --configuration Debug '
                '-p:IncludeIntegrationHarness=true'
            ),
        )

    def test_ci_sets_up_the_sdk_before_msbuild_backed_repository_contracts(self):
        workflow = CI_WORKFLOW.read_text(encoding='utf-8')
        build_job = workflow.split('  integration-shard:', maxsplit=1)[0]
        self.assertLess(
            build_job.index('- name: Set up .NET SDK'),
            build_job.index('- name: Verify repository contracts'),
        )


if __name__ == '__main__':
    unittest.main()
