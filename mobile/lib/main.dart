import 'package:flutter/material.dart';

import 'app/app_controller.dart';
import 'demo/demo_plans.dart';
import 'state/plan_controller.dart';
import 'sync/store_factory.dart';
import 'ui/home_page.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Development convenience: open the app with ?demo=apartment or ?demo=lattice to start from a demo plan.
  final demo = Uri.base.queryParameters['demo'];
  final editor = PlanController(
    model: switch (demo) {
      'apartment' => DemoPlans.apartment(),
      'lattice' => DemoPlans.lattice(),
      _ => null,
    },
  );

  final app = AppController(store: await openLocalStore(), editor: editor);
  await app.restore();
  runApp(BuildingDraftApp(app: app));
}

class BuildingDraftApp extends StatelessWidget {
  const BuildingDraftApp({super.key, required this.app});

  final AppController app;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Building Draft',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(colorSchemeSeed: const Color(0xFF2F5D8A), useMaterial3: true),
      darkTheme: ThemeData(colorSchemeSeed: const Color(0xFF2F5D8A), useMaterial3: true, brightness: Brightness.dark),
      home: HomePage(app: app),
    );
  }
}
