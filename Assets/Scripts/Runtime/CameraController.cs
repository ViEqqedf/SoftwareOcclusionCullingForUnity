using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ViE.SOC.Runtime {
    public class CameraController : MonoBehaviour {
        [SerializeField]
        private float mouseSensitivity = 5f;

        [SerializeField]
        private float movementSpeed = 5f;

        [SerializeField]
        private float _height = 5f;

        private float _xRotation = 0f;
        private float _currentHeight;

        private void Start() {
            Cursor.lockState = CursorLockMode.Locked;

            _currentHeight = transform.parent.position.y;
        }

        private void Update() {
#if UNITY_EDITOR || UNITY_STANDALONE
            float horizontal = Input.GetAxis("Horizontal");
            float vertical = Input.GetAxis("Vertical");
            float rotateX = Input.GetAxis("Mouse X") * mouseSensitivity * 100 * Time.deltaTime;
            float rotateY = Input.GetAxis("Mouse Y") * mouseSensitivity * 100 * Time.deltaTime;
#else
            float horizontal = 0;
            float vertical = 0;
            float rotateX = 0;
            float rotateY = 0;

            foreach (var touch in Input.touches) {
                if (touch.position.x < Screen.width / 2) {
                    Vector2 touchDeltaPosition = touch.deltaPosition;
                    horizontal = touchDeltaPosition.x;
                    vertical = touchDeltaPosition.y;
                }

                if (touch.position.x >= Screen.width / 2) {
                    Vector2 touchDeltaPosition = touch.deltaPosition;
                    rotateX = touchDeltaPosition.x * mouseSensitivity * Time.deltaTime;
                    rotateY = touchDeltaPosition.y * mouseSensitivity * Time.deltaTime;
                }
            }

            // if (Input.touchCount >= 1 && Input.GetTouch(0).phase == TouchPhase.Moved) {
            //     Vector2 touchDeltaPosition = Input.GetTouch(0).deltaPosition;
            //     rotateX = touchDeltaPosition.x * mouseSensitivity * Time.deltaTime;
            //     rotateY = touchDeltaPosition.y * mouseSensitivity * Time.deltaTime;
            // }
            //
            //
            // if (Input.touchCount >= 2 && Input.GetTouch(1).phase == TouchPhase.Moved) {
            //     Vector2 touchDeltaPosition = Input.GetTouch(1).deltaPosition;
            //     horizontal = touchDeltaPosition.x;
            //     vertical = touchDeltaPosition.y;
            // }
#endif

            rotateX = Mathf.Clamp(rotateX, -10, 10);
            rotateY = Mathf.Clamp(rotateY, -10, 10);
            Rotate(rotateX, rotateY);

            Vector3 movement = CalculateMovement(horizontal, vertical);
            Vector3 position = transform.parent.position;

            _currentHeight = Mathf.Lerp(_currentHeight, GetHeight(), movementSpeed * Time.deltaTime);

            position += (horizontal * transform.right + vertical * transform.forward) * movementSpeed * Time.deltaTime;
            // position.y = _currentHeight;

            MoveToPoint(position);
        }


        private void Rotate(float mouseX, float mouseY) {
            _xRotation -= mouseY;
            _xRotation = Mathf.Clamp(_xRotation, -90f, 90f);

            transform.localRotation = Quaternion.Euler(_xRotation, 0f, 0f);
            transform.parent.Rotate(Vector3.up * mouseX);
        }

        private Vector3 CalculateMovement(float horizontal, float vertical) {
            Vector3 movement = transform.right * horizontal + transform.forward * vertical;

            movement.y = 0f;

            return movement.normalized;
        }

        private void MoveToPoint(Vector3 targetPoint) {
            Vector3 start = transform.parent.position;
            start.y = targetPoint.y;

            Ray ray = new Ray(start, targetPoint - start);

            if (Physics.Raycast(ray, out RaycastHit hit, 3f)) {
                return;
            }

            transform.parent.position = targetPoint;
        }

        private float GetHeight() {
            Ray ray = new Ray(transform.parent.position + Vector3.up * 20, -transform.parent.up);

            if (Physics.Raycast(ray, out RaycastHit hit)) {
                return hit.point.y + _height;
            }

            return _height;
        }
    }
}