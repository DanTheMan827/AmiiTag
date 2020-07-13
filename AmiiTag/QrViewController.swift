//
//  QrViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/30/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

class QrViewController: UIViewController {
    var _qrData = ""
    fileprivate var loaded = false
    var qrData: String {
        get {
            return _qrData;
        }
        set(value) {
            _qrData = value
            if loaded {
                showQrCode()
            }
        }
    }
    
    var fileName = "Amiibo"
    
    @IBOutlet var QrImageView: UIImageView!
    @IBAction func shareQrTap(_ sender: Any) {
        // image to share
        guard let image = QrImageView.image else {
            return
        }
        
        let contentURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent(fileName)
            .appendingPathExtension("png")
        
        guard let _ = try? image.pngData()?.write(to: contentURL) else {
            return
        }

        // set up activity view controller
        let imageToShare = [ contentURL ]
        let activityViewController = UIActivityViewController(activityItems: imageToShare, applicationActivities: nil)
        activityViewController.popoverPresentationController?.sourceView = self.view // so that iPads won't crash
        activityViewController.completionWithItemsHandler = { (activityType, completed:Bool, returnedItems:[Any]?, error: Error?) in
           try? FileManager.default.removeItem(at: contentURL)
        }

        // present the view controller
        self.present(activityViewController, animated: true)
    }
    
    func generateQRCode(from data: String, withFactor factor: Float = 1) -> UIImage? {
        if let filter = CIFilter(name: "CIQRCodeGenerator") {
            filter.setValue("L", forKey: "inputCorrectionLevel")
            filter.setValue(data.data(using: .ascii), forKey: "inputMessage")
            let transform = CGAffineTransform(scaleX: CGFloat(factor), y: CGFloat(factor))
            

            if let output = filter.outputImage?.transformed(by: transform) {
                return UIImage(ciImage: output)
            }
        }

        return nil
    }
    
    func showQrCode() {
        QrImageView.image = generateQRCode(from: qrData, withFactor: 5)
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        loaded = true;
    }
}
