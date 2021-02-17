//
//  QrScannerView.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/30/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import AVFoundation
import UIKit

class ScannerViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate, ScannerViewControllerDelegate {
    var delegate: ScannerViewControllerDelegate?
    var completionHandler: ((Result<TagDump, Error>) -> Void)? = nil
    var captureSession: AVCaptureSession!
    var previewLayer: AVCaptureVideoPreviewLayer!

    override func viewDidLoad() {
        super.viewDidLoad()

        view.backgroundColor = UIColor.black
        captureSession = AVCaptureSession()

        guard let videoCaptureDevice = AVCaptureDevice.default(for: .video) else { return }
        let videoInput: AVCaptureDeviceInput

        do {
            videoInput = try AVCaptureDeviceInput(device: videoCaptureDevice)
        } catch {
            return
        }

        if (captureSession.canAddInput(videoInput)) {
            captureSession.addInput(videoInput)
        } else {
            failed()
            return
        }

        let metadataOutput = AVCaptureMetadataOutput()

        if (captureSession.canAddOutput(metadataOutput)) {
            captureSession.addOutput(metadataOutput)

            metadataOutput.setMetadataObjectsDelegate(self, queue: DispatchQueue.main)
            metadataOutput.metadataObjectTypes = [.qr]
        } else {
            failed()
            return
        }

        previewLayer = AVCaptureVideoPreviewLayer(session: captureSession)
        previewLayer.frame = view.layer.bounds
        previewLayer.videoGravity = .resizeAspectFill
        view.layer.addSublayer(previewLayer)

        captureSession.startRunning()
    }

    func failed() {
        self.completionHandler?(.failure(AmiiTagError(description: "Unable to activate camera")))
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)

        if (captureSession?.isRunning == false) {
            captureSession.startRunning()
        }
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)

        if (captureSession?.isRunning == true) {
            captureSession.stopRunning()
        }
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        captureSession.stopRunning()

        if let metadataObject = metadataObjects.first {
            guard let readableObject = metadataObject as? AVMetadataMachineReadableCodeObject else { return }
            guard let stringValue = readableObject.stringValue else { return }
            AudioServicesPlaySystemSound(SystemSoundID(kSystemSoundID_Vibrate))
            delegate?.scannerCodeFound(code: stringValue)
        }

        dismiss(animated: true)
    }

    override var prefersStatusBarHidden: Bool {
        return true
    }

    override var supportedInterfaceOrientations: UIInterfaceOrientationMask {
        return .portrait
    }
    
    func scannerCodeFound(code: String) {
        guard let data = try? Data(base64Encoded: code, options: .ignoreUnknownCharacters) else {
            dismiss(animated: true) {
                self.completionHandler?(.failure(AmiiTagError(description: "Failed to decode QR code")))
            }
            return
        }
        
        if (data.count == 532 || data.count == 540 || data.count == 572) {
            if let dump = TagDump(data: data) {
                dismiss(animated: true) {
                    self.completionHandler?(.success(dump))
                }
            } else {
                dismiss(animated: true) {
                    self.completionHandler?(.failure(AmiiTagError(description: "Invalid data in QR code")))
                }
            }
        } else {
            dismiss(animated: true) {
                self.completionHandler?(.failure(AmiiTagError(description: "Invalid data in QR code")))
            }
        }
    }
    
    static func ShowScanner(PresentingViewController presentingVc: UIViewController, completionHandler: @escaping (Result<TagDump, Error>) -> Void) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "ScannerView") as? ScannerViewController else {
            return
        }
        vc.completionHandler = completionHandler
        vc.delegate = vc
        presentingVc.present(vc, animated: true)
    }
}

protocol ScannerViewControllerDelegate {
    func scannerCodeFound(code: String)
}
