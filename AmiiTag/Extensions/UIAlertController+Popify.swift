//
//  UIAlertController+Popify.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 5/26/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

extension UIAlertController {
    func Popify(view: UIView) -> UIAlertController {
        if let popover = self.popoverPresentationController {
            popover.sourceView = view
            popover.sourceRect = CGRect(x: view.bounds.midX, y: view.bounds.midY, width: 0, height: 0)
            popover.permittedArrowDirections = []
        }
        
        return self
    }
}
