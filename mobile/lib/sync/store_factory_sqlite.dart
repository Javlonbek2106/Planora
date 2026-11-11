import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import 'local_store.dart';
import 'sqlite_store.dart';

Future<LocalStore> openLocalStore() async {
  final directory = await getApplicationSupportDirectory();
  return SqliteLocalStore.open(p.join(directory.path, 'building_draft.db'));
}
