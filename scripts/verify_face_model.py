"""CPU inference smoke check of the exact model packaged in the APK (no user photos)."""
from pathlib import Path
import numpy as np
from tflite_runtime.interpreter import Interpreter

model = Path(__file__).resolve().parents[1] / 'app/src/main/assets/mobile_facenet.tflite'
interpreter = Interpreter(model_path=str(model), num_threads=2)
interpreter.allocate_tensors()
inputs = interpreter.get_input_details()
outputs = interpreter.get_output_details()
assert len(inputs) == 2 and len(outputs) == 1
for tensor in inputs:
    assert tensor['shape'].tolist() == [1, 3, 112, 112]
    assert tensor['dtype'] == np.float32
assert outputs[0]['shape'].tolist() == [2, 128]
rng = np.random.default_rng(42)
fixture = rng.random((1, 3, 112, 112), dtype=np.float32)
for tensor in inputs:
    interpreter.set_tensor(tensor['index'], fixture)
interpreter.invoke()
embeddings = interpreter.get_tensor(outputs[0]['index'])
assert np.isfinite(embeddings).all()
assert (np.linalg.norm(embeddings, axis=1) > 1e-8).all()
assert np.allclose(embeddings[0], embeddings[1], rtol=1e-4, atol=1e-5)
print('CPU inference passed: two RGB NCHW inputs, nonzero 128-dimensional identical-image embeddings.')
